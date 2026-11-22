import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {spawnSync} from 'node:child_process';
import {
  sandboxDockerArgs,parseSandboxReport,runSandboxCompatibility,main,
} from '../tools/m4/windows_chromium_sandbox_qualification.mjs';
import {
  CASES,BROWSER_ARGS,BROWSER_CODES,BROWSER_KINDS,classifyBrowser,
} from '../tools/m4/windows_chromium_sandbox_guest.mjs';

const NAME='ca-win-m47-012345abcdef';
const ID='sha256:'+'a'.repeat(64);
const DIGEST='mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64);
const META={Id:ID,RepoDigests:[DIGEST],Os:'linux',Architecture:'amd64'};
const INFO={OSType:'linux',OperatingSystem:'Docker Desktop',
  PidsLimit:true,MemoryLimit:true};
const GOOD=Object.fromEntries(CASES.map(key=>[key,true]));
const BROWSER={binary:'HEADLESS_SHELL',dom:'PASS',sandbox:'PASS'};
function envelope(cases=GOOD,browser=BROWSER){
  return {schemaVersion:1,cases,browser};
}
function fakeDocker(overrides={}){
  const calls=[];let ps=0;
  function docker(args,timeout,input){
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')
      return {status:0,stdout:overrides.context??'desktop-linux'};
    if(args[0]==='context'&&args[1]==='inspect')
      return {status:0,stdout:JSON.stringify({
        Endpoints:{docker:{Host:overrides.remote?
          'tcp://other-host:2375':'npipe:////./pipe/dockerDesktopLinuxEngine'}},
      })};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify(INFO)};
    if(args[0]==='image')return {
      status:overrides.missingImage?1:0,stdout:JSON.stringify(META),
    };
    if(args[0]==='ps'){
      ps++;
      return {status:overrides.psError?1:0,
        stdout:ps===1?(overrides.collision?'existing':''):
          (overrides.leftover?'leftover':'')};
    }
    if(args[0]==='run')return {
      status:overrides.exit??0,
      stdout:overrides.stdout??JSON.stringify(
        envelope(overrides.cases??GOOD,overrides.browser??BROWSER)),
      stderr:overrides.stderr??'',
      timedOut:overrides.timedOut??false,
    };
    if(args[0]==='container'&&args[1]==='inspect')
      return {status:overrides.absent?1:0,stdout:JSON.stringify({
        'codingassistant.m47':overrides.badLabel?'somebody-else':NAME,
      })};
    if(args[0]==='rm')return {status:0,stdout:''};
    throw Error('Unexpected Docker command '+args.join(' '));
  }
  return {docker,calls};
}
test('sandbox probe fixed guest has 11 distinct documented checks',()=>{
  assert.equal(CASES.length,11);
  assert.equal(new Set(CASES).size,CASES.length);
  const guest=fs.readFileSync(new URL(
    '../tools/m4/windows_chromium_sandbox_guest.mjs',import.meta.url),'utf8');
  assert.ok(guest.includes('chrome://sandbox'));
  assert.ok(guest.includes('CA_M47_SANDBOXED_DOM_OK'));
  for(const k of CASES)assert.ok(guest.includes(k),k);
  for(const p of [
    'tools/m4/windows_chromium_sandbox_guest.mjs',
    'tools/m4/windows_chromium_sandbox_qualification.mjs',
  ]){
    const r=spawnSync(process.execPath,['--check',p],{
      encoding:'utf8',timeout:5000,
    });
    assert.equal(r.status,0,p+': '+r.stderr);
  }
});

test('browser runs without dangerous sandbox-disabling switches',()=>{
  for(const bypass of ['no-sandbox','disable-setuid-sandbox','no-zygote',
    'single-process','disable-seccomp-filter-sandbox'])
    assert.ok(!BROWSER_ARGS.some(a=>a.includes(bypass)),bypass);
  assert.ok(BROWSER_ARGS.includes('--dump-dom'));
  assert.ok(!BROWSER_ARGS.some(a=>a.startsWith('http:')));
  assert.ok(BROWSER_CODES.includes('CHROMIUM_SANDBOX_REJECTED'));
  assert.ok(BROWSER_KINDS.includes('HEADLESS_SHELL'));
});
test('Docker contract has immutable offline image and bounded no-mount isolation',()=>{
  const argv=sandboxDockerArgs(ID,NAME);
  for(const required of ['--rm','-i','--pull=never',
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2',
    '--ipc=private','--shm-size=256m',
    '--input-type=module','-','--run-fixed-sandbox-probe'])
    assert.ok(argv.includes(required),required);
  assert.ok(argv.includes(ID));
  assert.equal(argv.filter(a=>a==='--mount').length,0);
  for(const bad of ['--privileged','--pid=host','--ipc=host',
    '--network=host','--cap-add','--env-file','--mount','-v',
    '--volume','--device','--publish','--security-opt=seccomp=unconfined',
    '--security-opt=apparmor=unconfined'])
    assert.ok(!argv.includes(bad),bad);
  assert.ok(!argv.some(a=>a.startsWith('--pid=')));
  assert.throws(()=>sandboxDockerArgs('mcr:latest',NAME));
  assert.throws(()=>sandboxDockerArgs(ID,'unapproved-name'));
});
test('strict report rejects malformed, oversized, unknown, and forged categories',()=>{
  assert.deepEqual(parseSandboxReport(JSON.stringify(envelope())),
    {cases:GOOD,browser:BROWSER});
  const negative={...GOOD,chromium_sandbox_status_adequate:false};
  assert.equal(parseSandboxReport(JSON.stringify(
    envelope(negative,{...BROWSER,sandbox:'CHROMIUM_SANDBOX_STATUS_UNPROVEN'})))
    .cases.chromium_sandbox_status_adequate,false);
  for(const raw of [
    '', '{}','[]','false','{"blocked":true}',
    JSON.stringify({...envelope(),extra:'injected'}),
    JSON.stringify({...envelope(),schemaVersion:2}),
    JSON.stringify({...envelope(),cases:{...GOOD,extra:true}}),
    JSON.stringify({...envelope(),cases:{...GOOD,non_root_uid:'true'}}),
    JSON.stringify({...envelope(),browser:{...BROWSER,dom:'raw log'}}),
    JSON.stringify({...envelope(),browser:{...BROWSER,path:'/home/user'}}),
    JSON.stringify({...envelope(),browser:{...BROWSER,sandbox:0}}),
    '{unclosed',
    'x'.repeat(8000),
  ])assert.throws(()=>parseSandboxReport(raw),raw.slice(0,60));
});
test('Chrome error categorizer emits only allowlisted codes and no stderr',()=>{
  const cases=[
    [{error:{code:'ETIMEDOUT'}},'CHROMIUM_TIMEOUT'],
    [{error:{code:'ENOENT'}},'CHROMIUM_SPAWN_ENOENT'],
    [{error:{code:'EACCES'}},'CHROMIUM_SPAWN_EACCES'],
    [{error:{code:'EIO'}},'CHROMIUM_SPAWN_ERROR'],
    [{stderr:'error while loading shared libraries SECRET'},'CHROMIUM_SHARED_LIBRARY_ERROR'],
    [{stderr:'Failed to move to new namespace: Operation not permitted SECRET'},
      'CHROMIUM_SANDBOX_REJECTED'],
    [{stderr:'no usable sandbox!'},'CHROMIUM_SANDBOX_REJECTED'],
    [{stderr:'Resource temporarily unavailable'},'CHROMIUM_RESOURCE_EXHAUSTED'],
    [{signal:'SIGSYS',stderr:'SECRET'},'CHROMIUM_CRASH'],
    [{status:1,stderr:'SECRET'},'CHROMIUM_EXIT_NONZERO'],
  ];
  for(const [input,expected] of cases){
    const code=classifyBrowser(input);
    assert.equal(code,expected);
    assert.ok(BROWSER_CODES.includes(code));
    assert.ok(!code.includes('SECRET'));
  }
});
test('simulated positive Windows run never sets untrusted execution permission',()=>{
  const mock=fakeDocker();
  const result=runSandboxCompatibility({platform:'win32',
    env:{},name:NAME,docker:mock.docker});
  assert.equal(result.status,'SANDBOX_COMPATIBILITY_PASS');
  assert.equal(result.passedChecks,11);
  assert.equal(result.containerRemoved,true);
  assert.deepEqual(result.browser,BROWSER);
  for(const prop of [
    'modelInvoked','generatedSourceExecuted','sourceMounted',
    'untrustedBrowserQualified','staticWebRuntimeQualified',
    'generatedCodeExecutionEnabled'])
    assert.equal(result[prop],false,prop);
  const run=mock.calls.filter(x=>x.args[0]==='run');
  assert.equal(run.length,1);
  assert.equal(run[0].input,fs.readFileSync(new URL(
    '../tools/m4/windows_chromium_sandbox_guest.mjs',import.meta.url),'utf8'));
  assert.ok(!run[0].args.includes(run[0].input));
  assert.equal(mock.calls.filter(x=>x.args[0]==='rm').length,1);
  assert.ok(!mock.calls.some(x=>x.args[0]==='pull'||x.args[0]==='build'));
});
test('each failed sandbox/egress/secret/child/browser check blocks and cleans up',()=>{
  for(const key of CASES){
    const bad={...GOOD,[key]:false};
    const mock=fakeDocker({cases:bad,exit:2,
      browser:key==='chromium_sandbox_status_adequate'?
        {...BROWSER,sandbox:'CHROMIUM_SANDBOX_STATUS_UNPROVEN'}:BROWSER});
    const result=runSandboxCompatibility({platform:'win32',
      env:{},name:NAME,docker:mock.docker});
    assert.equal(result.status,'BLOCKED',key);
    assert.equal(result.passedChecks,10,key);
    assert.equal(result.containerRemoved,true,key);
    assert.match(result.reason,new RegExp(key));
    assert.equal(result.generatedCodeExecutionEnabled,false);
  }
});
test('missing image, remote Docker, collision and host overrides never run',()=>{
  for(const change of [{remote:true},{missingImage:true},{collision:true}]){
    const mock=fakeDocker(change);
    const r=runSandboxCompatibility({platform:'win32',env:{},
      name:NAME,docker:mock.docker});
    assert.equal(r.status,'BLOCKED');
    assert.equal(mock.calls.filter(x=>x.args[0]==='run').length,0);
  }
  const mock=fakeDocker();
  for(const options of [
    {platform:'linux',env:{}},{platform:'darwin',env:{}},
    {platform:'win32',env:{DOCKER_HOST:'tcp://bad'}},
    {platform:'win32',env:{DOCKER_CONTEXT:'remote'}},
  ])assert.equal(runSandboxCompatibility({
    ...options,name:NAME,docker:mock.docker}).status,'BLOCKED');
  assert.equal(mock.calls.length,0);
});
test('malformed guest, nonzero status, timeout, and ownership cleanup fail closed',()=>{
  for(const change of [
    {stdout:'{"blocked":true}',exit:2},
    {stdout:'',exit:125,stderr:'SECRET_PATH'},
    {exit:2},{timedOut:true},
    {badLabel:true},{leftover:true},{psError:true},
  ]){
    const mock=fakeDocker(change);
    const result=runSandboxCompatibility({
      platform:'win32',env:{},name:NAME,docker:mock.docker,
    });
    assert.equal(result.status,'BLOCKED',JSON.stringify(change));
    assert.equal(result.generatedCodeExecutionEnabled,false);
    assert.ok(!String(result.reason).includes('SECRET_PATH'));
  }
});
test('no arguments is NOT_RUN and never contacts Docker',()=>{
  const processResult=spawnSync(process.execPath,
    ['tools/m4/windows_chromium_sandbox_qualification.mjs'],{
      encoding:'utf8',timeout:5000,
    });
  assert.equal(processResult.status,0,processResult.stderr);
  assert.match(processResult.stdout,/NOT_RUN/);
});
