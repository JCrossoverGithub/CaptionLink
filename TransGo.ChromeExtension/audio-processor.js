"use strict";

class TransGoPcmProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.targetSampleRate = 16000;
    this.sourceSamplesPerTargetSample = sampleRate / this.targetSampleRate;
    this.sourceBuffer = [];
    this.sourcePosition = 0;
    this.pendingSamples = [];
    this.outputSamplesSent = 0;
    this.chunkSamples = 1600;
  }

  process(inputs) {
    const channels = inputs[0];

    if (!channels?.length || channels[0].length === 0) {
      return true;
    }

    const frameLength = channels[0].length;

    for (let frame = 0; frame < frameLength; frame += 1) {
      let mixed = 0;

      for (const channel of channels) {
        mixed += channel[frame] || 0;
      }

      this.sourceBuffer.push(mixed / channels.length);
    }

    this.resampleAvailableAudio();
    return true;
  }

  resampleAvailableAudio() {
    while (this.sourcePosition + 1 < this.sourceBuffer.length) {
      const leftIndex = Math.floor(this.sourcePosition);
      const fraction = this.sourcePosition - leftIndex;
      const left = this.sourceBuffer[leftIndex];
      const right = this.sourceBuffer[leftIndex + 1];
      const sample = left + ((right - left) * fraction);

      this.pendingSamples.push(sample);
      this.sourcePosition += this.sourceSamplesPerTargetSample;

      if (this.pendingSamples.length === this.chunkSamples) {
        this.emitChunk();
      }
    }

    const consumedSamples = Math.min(
      Math.floor(this.sourcePosition),
      this.sourceBuffer.length);

    if (consumedSamples > 0) {
      this.sourceBuffer = this.sourceBuffer.slice(consumedSamples);
      this.sourcePosition -= consumedSamples;
    }
  }

  emitChunk() {
    const pcm = new ArrayBuffer(this.chunkSamples * 2);
    const view = new DataView(pcm);

    for (let index = 0; index < this.chunkSamples; index += 1) {
      const clamped = Math.max(-1, Math.min(1, this.pendingSamples[index]));
      const value = clamped < 0
        ? Math.round(clamped * 32768)
        : Math.round(clamped * 32767);
      view.setInt16(index * 2, value, true);
    }

    const timestampMilliseconds = Math.round(
      this.outputSamplesSent * 1000 / this.targetSampleRate);

    this.outputSamplesSent += this.chunkSamples;
    this.pendingSamples = [];

    this.port.postMessage({
      pcm,
      timestampMilliseconds
    }, [pcm]);
  }
}

registerProcessor("transgo-pcm-processor", TransGoPcmProcessor);
