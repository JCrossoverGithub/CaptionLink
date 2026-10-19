import test from 'node:test';
import assert from 'node:assert/strict';
import {buildLauncher,catalogCandidate,guardString,shellQuote,EXPECTED_UNTRACKED,validateSessionTimeoutMs,EXPECTED_GP_DIFF,GP_BRANCH} from '../prepare-operator-review.mjs';
const sha='a'.repeat(64);
const modelLocks={asrCheckpoint:'/mnt/operator/asr.nemo',diarCheckpoint:'/mnt/operator/diar.nemo',asrSha:sha,diarSha:sha};
const manifest={schemaVersion:2,type:'speech.streaming.multitalker',adapterId:'captionlink.multitalker.parakeet.v0',version:'0.1.0',executionMode:'streaming-gateway'};
test('minimal launcher does not inherit HOME/PATH and keeps offline settings',()=>{
  const text=buildLauncher({python:'/opt/pinned/bin/python',hfHome:'/home/test/hf cache',driverDir:'/usr/lib/gpu',...modelLocks}).toString();
  assert.match(text,/export PATH=\/usr\/bin:\/bin/);
  assert.match(text,/export HF_HUB_OFFLINE=1/);
  assert.match(text,/HF_HOME='\/home\/test\/hf cache'/);
  assert.match(text,/GPULINK_ASR_CHECKPOINT='\/mnt\/operator\/asr.nemo'/);
  assert.match(text,/GPULINK_ASR_SHA256='a{64}'/);
  assert.match(text,/exec '\/opt\/pinned\/bin\/python' -I -B "\$1"/);
  assert.doesNotMatch(text,/source .*\.bashrc|\$HOME|eval /);
});
test('rejects control and newline characters in shell-facing operator values',()=>{
  assert.throws(()=>guardString('/tmp/x\nexport FOO=1'));
  assert.throws(()=>guardString('/tmp/x\0foo'));
  assert.throws(()=>buildLauncher({python:'python',hfHome:'/x',driverDir:'/y',...modelLocks}));
  assert.equal(shellQuote("/some/what's"),"'/some/what'\\''s'");
});
test('candidate uses current v1 catalog with explicit completion and three approved assets',()=>{
  const c=catalogCandidate({manifest,bash:'/bin/bash',bashSha:sha,launcher:'/operator/launcher',launcherSha:sha,adapter:'/operator/adapter.py',adapterSha:sha,service:'/operator/service.py',serviceSha:sha});
  assert.deepEqual(Object.keys(c),['schemaVersion','adapters']);
  assert.equal(c.adapters[0].completionMode,'explicit-text-v1');
  assert.equal(c.adapters[0].command.assets.length,3);
  assert.deepEqual(c.adapters[0].command.args,['/operator/launcher','/operator/adapter.py']);
  assert.throws(()=>catalogCandidate({manifest:{...manifest,type:'arbitrary'},bash:'/bin/bash',bashSha:sha,launcher:'/x',launcherSha:sha,adapter:'/y',adapterSha:sha,service:'/z',serviceSha:sha}));
});
test('expected staging paths contain only intended existing and new review files',()=>{
  assert.equal(EXPECTED_UNTRACKED.length,7);
  assert.equal(new Set(EXPECTED_UNTRACKED).size,7);
});

test('rejects missing or invalid checkpoint locks',()=>{
  assert.throws(()=>buildLauncher({python:'/opt/python',hfHome:'/x',driverDir:'/y',...modelLocks,asrSha:'BAD'}));
  assert.throws(()=>buildLauncher({python:'/opt/python',hfHome:'/x',driverDir:'/y'}));
});


test('operator timeout is opt-in, integer, and strictly bounded',()=>{
  assert.equal(validateSessionTimeoutMs(720000),720000);
  for(const n of [undefined, null, 0, 29999, 900001, 1.5, '720000']) {
    assert.throws(()=>validateSessionTimeoutMs(n));
  }
  const base={manifest,bash:'/bin/bash',bashSha:sha,launcher:'/operator/launcher',launcherSha:sha,
    adapter:'/operator/adapter.py',adapterSha:sha,service:'/operator/service.py',serviceSha:sha};
  const old=catalogCandidate(base);
  assert.equal(old.adapters[0].executionLimits,undefined);
  const approved=catalogCandidate({...base,sessionTimeoutMs:720000});
  assert.deepEqual(approved.adapters[0].executionLimits,{sessionTimeoutMs:720000});
  assert.equal(approved.adapters[0].completionMode,'explicit-text-v1');
  assert.equal(approved.adapters[0].command.assets.length,3);
  for(const n of [0,900001,1.5,'720000']) {
    assert.throws(()=>catalogCandidate({...base,sessionTimeoutMs:n}));
  }
});

test('review #03 guard names exactly the four approved GPULink source files',()=>{
  assert.equal(GP_BRANCH,'feature/operator-bounded-adapter-sessions');
  assert.equal(new Set(EXPECTED_GP_DIFF).size,4);
  assert.deepEqual([...EXPECTED_GP_DIFF].sort(),[
    'src/worker/adapter-subprocess-supervisor.mjs',
    'src/worker/approved-subprocess-adapters.mjs',
    'test/adapter-subprocess-supervisor.test.mjs',
    'test/approved-subprocess-adapters.test.mjs',
  ].sort());
});
