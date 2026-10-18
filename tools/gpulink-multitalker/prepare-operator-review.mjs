#!/usr/bin/env node
/**
 * GPULink Multitalker operator REVIEW candidate, not an installer or a sandbox.
 * Keeps the accepted SDK one-file package unmodified; creates a separate
 * operator-local sibling service + explicit environment launcher + catalog.
 * Nothing is activated, uploaded, or deployed.
 */
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {execFileSync, spawnSync} from 'node:child_process';
import {constants} from 'node:fs';
import {createReadStream} from 'node:fs';
import {chmod, copyFile, lstat, mkdir, readFile, realpath, rm, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

export const HEAD_GP = '48d10e97206ce94b2b6fbc3b750d6a7e6c3cb815';
export const HEAD_CL = 'd467c5b70de4dc6df84ff3014c2523c730ee6951';
export const CL_BRANCH = 'feature/gpulink-multitalker-adapter';
export const GP_BRANCH = 'feature/operator-bounded-adapter-sessions';
export const SESSION_MIN_MS = 30_000;
export const SESSION_MAX_MS = 900_000;
export const EXPECTED_GP_DIFF = [
  'src/worker/adapter-subprocess-supervisor.mjs',
  'src/worker/approved-subprocess-adapters.mjs',
  'test/adapter-subprocess-supervisor.test.mjs',
  'test/approved-subprocess-adapters.test.mjs',
];
export function validateSessionTimeoutMs(value) {
  if (!Number.isSafeInteger(value) || value < SESSION_MIN_MS || value > SESSION_MAX_MS) {
    throw new Error('invalid operator session timeout');
  }
  return value;
}
export const ENTRY = 'gpulink_multitalker_adapter.py';
export const SERVICE = 'multitalker_service.py';
export const EXPECTED_UNTRACKED = [
  'CaptionLink.LocalAsr.Multitalker/GPULINK_ADAPTER_CANDIDATE.md',
  'CaptionLink.LocalAsr.Multitalker/gpulink.adapter.json',
  'CaptionLink.LocalAsr.Multitalker/gpulink_multitalker_adapter.py',
  'tests/test_gpulink_multitalker_adapter.py',
  'tools/gpulink-multitalker/README.md',
  'tools/gpulink-multitalker/prepare-operator-review.mjs',
  'tools/gpulink-multitalker/test/prepare-operator-review.test.mjs',
];
export function guardString(s) {
  if (typeof s !== 'string' || s.length < 1 || s.length > 2048 || /[\x00-\x1f\x7f]/u.test(s)) {
    throw new Error('unsafe path or argument');
  }
  return s;
}
export function shellQuote(s) { return `'${guardString(s).replaceAll("'", "'\\''")}'`; }
export function buildLauncher({python, hfHome, driverDir, asrCheckpoint, diarCheckpoint, asrSha, diarSha}) {
  for (const p of [python, hfHome, driverDir, asrCheckpoint, diarCheckpoint]) {
    if (!path.isAbsolute(guardString(p))) throw new Error('operator setting must be absolute');
  }
  for (const digest of [asrSha, diarSha]) {
    if (!/^[a-f0-9]{64}$/u.test(digest)) throw new Error('invalid model lock digest');
  }
  return Buffer.from(
    '#!/bin/bash\nset -Eeuo pipefail\numask 077\n' +
    'export HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_DATASETS_OFFLINE=1\n' +
    'export HF_HUB_DISABLE_TELEMETRY=1 PYTHONDONTWRITEBYTECODE=1\n' +
    'export PATH=/usr/bin:/bin\n' +
    `export HF_HOME=${shellQuote(hfHome)}\n` +
    `export LD_LIBRARY_PATH=${shellQuote(driverDir)}\n` +
    `export GPULINK_ASR_CHECKPOINT=${shellQuote(asrCheckpoint)}\n` +
    `export GPULINK_DIAR_CHECKPOINT=${shellQuote(diarCheckpoint)}\n` +
    `export GPULINK_ASR_SHA256=${shellQuote(asrSha)}\n` +
    `export GPULINK_DIAR_SHA256=${shellQuote(diarSha)}\n` +
    `exec ${shellQuote(python)} -I -B "$1"\n`, 'utf8');
}
export function catalogCandidate({manifest, bash, bashSha, launcher, launcherSha,
  adapter, adapterSha, service, serviceSha, sessionTimeoutMs = undefined}) {
  if (manifest.schemaVersion !== 2 || manifest.type !== 'speech.streaming.multitalker' ||
      manifest.adapterId !== 'captionlink.multitalker.parakeet.v0' ||
      manifest.executionMode !== 'streaming-gateway') throw new Error('unexpected adapter manifest');
  for (const h of [bashSha, launcherSha, adapterSha, serviceSha]) {
    if (!/^[a-f0-9]{64}$/u.test(h)) throw new Error('invalid digest');
  }
  if (sessionTimeoutMs !== undefined) validateSessionTimeoutMs(sessionTimeoutMs);
  return {schemaVersion:1, adapters:[{manifest,
    completionMode:'explicit-text-v1',
    ...(sessionTimeoutMs === undefined ? {} : {executionLimits:{sessionTimeoutMs}}),
    command:{executable:bash,sha256:bashSha,args:[launcher,adapter],assets:[
      {path:launcher,sha256:launcherSha},
      {path:adapter,sha256:adapterSha},
      {path:service,sha256:serviceSha},
    ]},
  }]};
}
function args(argv) {
  const pathOptions = new Set(['--captionlink','--gpulink','--out','--python','--hf-home','--driver-dir','--asr-checkpoint','--diar-checkpoint']);
  const valid = new Set([...pathOptions,'--session-timeout-ms']);
  const obj = {};
  if (argv.length !== pathOptions.size*2 && argv.length !== valid.size*2) {
    throw new Error('eight required path pairs, optional --session-timeout-ms');
  }
  for (let i=0;i<argv.length;i+=2) {
    if (!valid.has(argv[i]) || obj[argv[i]] !== undefined || !argv[i+1]) throw new Error('invalid CLI option');
    obj[argv[i]]=guardString(argv[i+1]);
  }
  if ([...pathOptions].some(k=>obj[k] === undefined || !path.isAbsolute(obj[k]))) {
    throw new Error('eight absolute paths required');
  }
  if (obj['--session-timeout-ms'] !== undefined) {
    if (!/^[1-9][0-9]*$/u.test(obj['--session-timeout-ms'])) {
      throw new Error('invalid operator session timeout');
    }
    obj['--session-timeout-ms'] = validateSessionTimeoutMs(Number(obj['--session-timeout-ms']));
  }
  return obj;
}
function git(repo, ...cmd) {
  return execFileSync('git',['-C',repo,...cmd],{encoding:'utf8',timeout:10000}).trimEnd();
}
function verifyRepo(cl,gp) {
  assert.equal(git(cl,'rev-parse','HEAD'), HEAD_CL, 'CaptionLink HEAD differs');
  assert.equal(git(cl,'branch','--show-current'), CL_BRANCH, 'CaptionLink branch differs');
  assert.equal(git(gp,'rev-parse','HEAD'), HEAD_GP, 'GPULink HEAD differs');
  assert.equal(git(gp,'branch','--show-current'),GP_BRANCH,'GPULink branch differs');
  assert.deepEqual(git(gp,'diff','HEAD','--name-only').split('\n').filter(Boolean).sort(),
    [...EXPECTED_GP_DIFF].sort(),'GPULink must contain exactly the four approved edits');
  assert.equal(git(gp,'diff','--cached','--name-only'),'','GPULink staged changes forbidden');
  assert.equal(git(gp,'ls-files','--others','--exclude-standard'),'','GPULink untracked files forbidden');
  assert.ok(['https://github.com/JCrossoverGithub/GPULink.git',
    'git@github.com:JCrossoverGithub/GPULink.git'].includes(git(gp,'remote','get-url','origin')),
    'GPULink origin changed');
  const changed = git(cl,'diff','--name-only').split('\n').filter(Boolean);
  assert.deepEqual(changed, ['CaptionLink.LocalAsr.Multitalker/multitalker_service.py'],
    'only approved model-loading service change allowed');
  assert.equal(git(cl,'diff','--cached','--name-only'),'','CaptionLink staged modifications');
  const untracked=git(cl,'ls-files','--others','--exclude-standard').split('\n').filter(Boolean).sort();
  assert.deepEqual(untracked, [...EXPECTED_UNTRACKED].sort(), 'unexpected CaptionLink untracked files');
  const remote=git(cl,'remote','get-url','origin');
  assert.ok(['https://github.com/JCrossoverGithub/CaptionLink.git',
    'git@github.com:JCrossoverGithub/CaptionLink.git'].includes(remote),'CaptionLink origin changed');
}
async function hashFile(file, min=1, max=Number.MAX_SAFE_INTEGER, allowSymlink=false) {
  const stat=await lstat(file);
  if ((!allowSymlink && stat.isSymbolicLink()) || !(await lstat(await realpath(file))).isFile()) {
    throw new Error('non-regular or symlinked source');
  }
  const actual=await realpath(file);
  const size=(await lstat(actual)).size;
  if (size<min || size>max) throw new Error('unexpected file size');
  const sha=createHash('sha256');
  for await (const buf of createReadStream(actual)) sha.update(buf);
  return {sha256:sha.digest('hex'),bytes:size,realpath:actual};
}
async function checkedDirectory(p) {
  const stat=await lstat(p);
  if (!stat.isDirectory() || stat.isSymbolicLink() || (stat.mode & 0o022)) {
    throw new Error('operator directory must be non-symlink and not group/other writable');
  }
}
async function runSdk(cli, verb, options) {
  const result=spawnSync(process.execPath,[cli,verb,...options],{
    encoding:'utf8',timeout:20000,maxBuffer:1024*1024,env:{...process.env,NODE_OPTIONS:''},
  });
  if (result.error || result.status!==0) {
    throw new Error(`Adapter SDK ${verb} failed: ${String(result.stderr||result.error?.message).slice(0,300)}`);
  }
  process.stdout.write(result.stdout);
}
async function prepare(options) {
  const cl=await realpath(options['--captionlink']);
  const gp=await realpath(options['--gpulink']);
  verifyRepo(cl,gp);
  const gpCode = {};
  for (const p of EXPECTED_GP_DIFF) {
    gpCode[p] = (await hashFile(path.join(gp,p),1,4*1024*1024)).sha256;
  }
  const supText = await readFile(path.join(gp,EXPECTED_GP_DIFF[0]),'utf8');
  const catalogText = await readFile(path.join(gp,EXPECTED_GP_DIFF[1]),'utf8');
  assert.match(supText,/MAX_SESSION_TIMEOUT_MS = 900_000/,'bounded supervisor missing');
  assert.match(catalogText,/MAX_APPROVED_SESSION_TIMEOUT_MS = 900_000/,'approved catalog bound missing');
  assert.match(catalogText,/sessionTimeoutMs: executionLimits\?\.sessionTimeoutMs \?\? 30_000/,
    'supervisor propagation missing');
  const sessionTimeoutMs = options['--session-timeout-ms'];
  if (sessionTimeoutMs === undefined) throw new Error('review #03 requires explicit session timeout');
  validateSessionTimeoutMs(sessionTimeoutMs);
  const source=path.join(cl,'CaptionLink.LocalAsr.Multitalker');
  const program=path.join(source,ENTRY);
  const service=path.join(source,SERVICE);
  const recipe=JSON.parse(await readFile(path.join(source,'gpulink.adapter.json'),'utf8'));
  assert.equal(recipe.entry,ENTRY);
  assert.equal(recipe.completionMode,'explicit-text-v1');
  assert.equal((await hashFile(program,1,4*1024*1024)).sha256,
    '5a046ed2398aa409016fcde517448ab2797766400d56e437f04fa4b4402422c1',
    'staged adapter is not the accepted GLIP candidate');
  await hashFile(service,1,4*1024*1024);
  const cli=path.join(gp,'packages/gpulink-adapter-sdk/bin/package.mjs');
  await hashFile(cli,1,1024*1024);
  const python=options['--python'];
  const hfHome=options['--hf-home'];
  const driverDir=options['--driver-dir'];
  await checkedDirectory(hfHome);
  await checkedDirectory(driverDir);
  const pyInfo=await hashFile(python,1,64*1024*1024,true);
  const asrInfo=await hashFile(options['--asr-checkpoint'],100*1024*1024,8*1024*1024*1024,true);
  const diarInfo=await hashFile(options['--diar-checkpoint'],100*1024*1024,8*1024*1024*1024,true);
  const bash=await realpath('/bin/bash');
  const bashInfo=await hashFile(bash,1,64*1024*1024);
  const out=options['--out'];
  const parent=await realpath(path.dirname(out));
  await checkedDirectory(parent);
  if (path.dirname(out)!==parent || path.basename(out)==='.' || path.basename(out)==='..') {
    throw new Error('output parent must use canonical non-symlink directory');
  }
  if ([cl,gp].some(repo=>out===repo||out.startsWith(repo+path.sep))) throw new Error('output cannot be inside repositories');
  if (out===parent) throw new Error('output must be new directory');
  let created=false;
  try {
    await mkdir(out,{mode:0o700}); created=true;
    const sdkBundle=path.join(out,'author-bundle');
    await runSdk(cli,'pack',['--source',source,'--out',sdkBundle]);
    await runSdk(cli,'inspect',['--bundle',sdkBundle]);
    const lock=JSON.parse(await readFile(path.join(sdkBundle,'gpulink.adapter.lock.json'),'utf8'));
    assert.equal(lock.entry,ENTRY);
    assert.equal(lock.completionMode,'explicit-text-v1');
    const operator=path.join(out,'operator');
    await mkdir(operator,{mode:0o700});
    const stagedAdapter=path.join(operator,ENTRY);
    const stagedService=path.join(operator,SERVICE);
    const launcher=path.join(operator,'operator-launcher.sh');
    await copyFile(path.join(sdkBundle,ENTRY),stagedAdapter,constants.COPYFILE_EXCL);
    await copyFile(service,stagedService,constants.COPYFILE_EXCL);
    await chmod(stagedAdapter,0o600);await chmod(stagedService,0o600);
    const adapterInfo=await hashFile(stagedAdapter,1,4*1024*1024);
    const serviceInfo=await hashFile(stagedService,1,4*1024*1024);
    assert.equal(adapterInfo.sha256,lock.sha256,'packed adapter digest mismatch');
    assert.equal(serviceInfo.sha256,(await hashFile(service,1,4*1024*1024)).sha256);
    const launcherBytes=buildLauncher({python,hfHome,driverDir,
      asrCheckpoint:asrInfo.realpath, diarCheckpoint:diarInfo.realpath,
      asrSha:asrInfo.sha256, diarSha:diarInfo.sha256});
    await writeFile(launcher,launcherBytes,{flag:'wx',mode:0o700});
    const launcherInfo=await hashFile(launcher,1,4*1024*1024);
    const catalog=catalogCandidate({manifest:lock.manifest,bash,bashSha:bashInfo.sha256,
      launcher,launcherSha:launcherInfo.sha256,adapter:stagedAdapter,
      adapterSha:adapterInfo.sha256,service:stagedService,serviceSha:serviceInfo.sha256,
      sessionTimeoutMs});
    const catalogFile=path.join(operator,'catalog.candidate.json');
    await writeFile(catalogFile,JSON.stringify(catalog,null,2)+'\n',{flag:'wx',mode:0o600});
    // Validate with GPULink's real existing catalog loader. This loads files only;
    // it never starts an adapter, enables a worker, or executes model code.
    const {loadApprovedSubprocessSessionAdapters}=await import(pathToFileURL(
      path.join(gp,'src/worker/approved-subprocess-adapters.mjs')).href);
    const verified=await loadApprovedSubprocessSessionAdapters(catalogFile);
    assert.equal(verified.length,1,'catalog loader rejected candidate');
    const readBackCatalog=JSON.parse(await readFile(catalogFile,'utf8'));
    assert.equal(readBackCatalog.adapters[0].executionLimits?.sessionTimeoutMs,sessionTimeoutMs);
    const receipt={schemaVersion:1,status:'OPERATOR_REVIEW_ONLY_NOT_INSTALLED',
      source:{captionLinkHead:HEAD_CL,gpuLinkHead:HEAD_GP,gpuLinkBranch:GP_BRANCH,
        gpuLinkCandidateFileHashes:gpCode,adapter:adapterInfo.sha256,service:serviceInfo.sha256},
      executionLimits:{sessionTimeoutMs},
      sdkBundleLock:lock,operatorFiles:{launcher:launcherInfo.sha256,catalog:(await hashFile(catalogFile)).sha256},
      operatorRuntime:{python,pythonResolved:pyInfo.realpath,pythonSha256:pyInfo.sha256,
        bash,bashSha256:bashInfo.sha256,hfHome,driverDir},
      models:{asr:{path:options['--asr-checkpoint'],...asrInfo},
        diar:{path:options['--diar-checkpoint'],...diarInfo}},
      limitations:['No signed publisher identity or package trust','Model checkpoint hashes recorded but NOT enforced by GPULink catalog',
        'The GPULink adapter verifies SHA-256 at load time and requests restore_from for approved checkpoints',
        'Checkpoint directories must remain operator-protected to prevent mutation after verification',
        'Python/native dependencies and CUDA libraries not transitively locked',
        '600-second paced loopback acceptance passed with review #02-derived temporary catalog, not review #03',
        'Review #03 has not yet passed physical GPU acceptance',
        'No installation, worker enrollment, multi-tenant isolation, GPU cleanup authority, or cross-machine acceptance']};
    await writeFile(path.join(out,'operator-review.json'),JSON.stringify(receipt,null,2)+'\n',{
      flag:'wx',mode:0o600});
    verifyRepo(cl,gp);
    console.log('PASS: SDK pack + inspect, sibling service hashes, catalog loader and baseline guards');
    console.log(`REVIEW_ONLY_OUTPUT: ${out}`);
    console.log('NOT INSTALLED: no worker catalog changed, no model or subprocess started');
  } catch (error) {
    if (created) await rm(out,{recursive:true,force:true});
    throw error;
  }
}
const self=process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url);
if (self) {
  try {await prepare(args(process.argv.slice(2)));}
  catch (err) {console.error(`STOP: ${err?.message ?? 'operator review failed'}`);process.exitCode=1;}
}
