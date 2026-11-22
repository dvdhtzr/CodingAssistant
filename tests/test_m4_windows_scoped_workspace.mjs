import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {
  setupFixture,verifyFixturePath,workspaceDockerArgs,
  hostAudit,parseWorkspaceResults,runWorkspace,main,
} from '../tools/m4/windows_scoped_workspace_qualification.mjs';
import {CASES,FIXTURE} from '../tools/m4/windows_scoped_workspace_guest.mjs';

const NAME='ca-win-m43-012345abcdef';
const ID='sha256:'+'a'.repeat(64);
const DIGEST='mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64);
const INFO={OSType:'linux',OperatingSystem:'Docker Desktop',PidsLimit:true,MemoryLimit:true};
const IMAGE={Id:ID,RepoDigests:[DIGEST],Os:'linux',Architecture:'amd64'};
const successful=Object.fromEntries(CASES.map(k=>[k,true]));
function response(status,stdout=''){return {status,stdout};}
function mockDocker(changes={}){
  const calls=[];let ps=0;
  const docker=(args,timeout,input)=>{
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')
      return response(0,'desktop-linux');
    if(args[0]==='context'&&args[1]==='inspect')
      return response(0,JSON.stringify({Endpoints:{docker:{Host:
        changes.remote?'tcp://remote:2375':'npipe:////./pipe/dockerDesktopLinuxEngine'}}}));
    if(args[0]==='info')return response(0,JSON.stringify(INFO));
    if(args[0]==='image')return response(changes.imageMissing?1:0,JSON.stringify(IMAGE));
    if(args[0]==='ps'){
      ps+=1;
      return response(ps===1?0:(changes.cleanupFailure?1:0),
        ps===1?(changes.collision?'collision':''):(changes.containerLeft?'container-id':''));
    }
    if(args[0]==='run'){
      const mount=args[args.indexOf('--mount')+1];
      const source=mount.slice('type=bind,source='.length).split(',target=/workspace')[0];
      if(changes.mutate!==false){
        fs.writeFileSync(path.join(source,'seed.txt'),FIXTURE.seedAfter);
        fs.writeFileSync(path.join(source,'index.html'),FIXTURE.html);
        fs.writeFileSync(path.join(source,'app.js'),FIXTURE.js);
        fs.writeFileSync(path.join(source,'child.txt'),FIXTURE.child);
        if(process.platform!=='win32'){
          fs.symlinkSync('/etc/passwd',path.join(source,'escape'));
        }
        if(changes.siblingTamper)
          fs.writeFileSync(path.join(path.dirname(source),'outside','keep.txt'),'BAD');
      }
      return response(changes.guestCode??0,
        JSON.stringify(changes.cases??successful));
    }
    if(args[0]==='container'&&args[1]==='inspect')
      return response(0,JSON.stringify({'codingassistant.m43':
        changes.wrongOwner?'WRONG':NAME}));
    if(args[0]==='rm')return response(0);
    throw Error('Unexpected simulated Docker subcommand '+args.join(' '));
  };
  return {docker,calls};
}

test('static M4.3 guest is exactly 17 reviewed cases and syntactically valid',()=>{
  assert.equal(CASES.length,17);
  assert.equal(new Set(CASES).size,CASES.length);
  const source=fs.readFileSync(new URL(
    '../tools/m4/windows_scoped_workspace_guest.mjs',import.meta.url),'utf8');
  for(const required of [
    '/workspace/seed.txt','/workspace/index.html','/workspace/app.js',
    '/workspace/../outside/keep.txt','/workspace/escape',
    'trusted_chromium_external_js_click','child_system_write_denied',
    '--dump-dom','--no-sandbox',
  ])assert.ok(source.includes(required),required);
  for(const forbidden of ['git push','npm install','session/prompt',
    'host.docker.internal','import {browserCandidatePaths}'])
    assert.ok(!source.includes(forbidden),forbidden);
  const syntax=spawnSync(process.execPath,
    ['--check','tools/m4/windows_scoped_workspace_guest.mjs'],
    {encoding:'utf8',timeout:5000});
  assert.equal(syntax.status,0,syntax.stderr);
});

test('disposable fixture mount has exact unique OS temp parent and no .git',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m43-'));
  try{
    const fixture=setupFixture(root);
    assert.equal(verifyFixturePath(fixture.approved),
      fs.realpathSync(fixture.approved));
    assert.equal(fs.readFileSync(path.join(fixture.outside,'keep.txt'),'utf8'),
      FIXTURE.outside);
    const args=workspaceDockerArgs(ID,NAME,fixture.approved);
    for(const flag of ['--pull=never','--network=none','--read-only',
      '--cap-drop=all','--security-opt=no-new-privileges',
      '--user=1000:1000','--pids-limit=96','--memory=2g',
      '--cpus=2','--mount','--workdir=/workspace',
      '--input-type=module','-','--run-fixed-workspace'])
      assert.ok(args.includes(flag),flag);
    assert.equal(args.filter(a=>a==='--mount').length,1);
    const mount=args[args.indexOf('--mount')+1];
    assert.equal(mount,'type=bind,source='+fs.realpathSync(fixture.approved)+
      ',target=/workspace,bind-propagation=rprivate');
    for(const bad of ['--privileged','--network=host','--pid=host',
      '--publish','-p','--env-file','--device','--volume','-v'])
      assert.ok(!args.includes(bad),bad);
    assert.ok(!args.some(x=>x.startsWith('--pid=')));
    assert.throws(()=>workspaceDockerArgs('latest',NAME,fixture.approved));
    assert.throws(()=>workspaceDockerArgs(ID,'bad',fixture.approved));
    fs.mkdirSync(path.join(fixture.approved,'.git'));
    assert.throws(()=>workspaceDockerArgs(ID,NAME,fixture.approved));
  }finally{fs.rmSync(root,{force:true,recursive:true});}
});

test('only freshly created exact temp workspace is acceptable',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'other-m43-'));
  try{
    const approved=path.join(root,'approved');
    fs.mkdirSync(approved);
    fs.writeFileSync(path.join(approved,'seed.txt'),FIXTURE.seedBefore);
    fs.writeFileSync(path.join(approved,'notes.txt'),FIXTURE.notes);
    assert.throws(()=>verifyFixturePath(approved));
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('workspace guest output must contain 17 exact Boolean observations',()=>{
  assert.deepEqual(parseWorkspaceResults(JSON.stringify(successful)),successful);
  for(const bad of [
    '', '{}', '[]','false','{"blocked":true}',
    JSON.stringify({...successful,extra:true}),
    JSON.stringify({...successful,[CASES[1]]:'true'}),
    JSON.stringify(Object.fromEntries(Object.entries(successful).slice(1))),
    'x'.repeat(6000),
  ])assert.throws(()=>parseWorkspaceResults(bad));
  const fail={...successful,system_write_denied:false};
  assert.equal(parseWorkspaceResults(JSON.stringify(fail)).system_write_denied,false);
});

test('independent host audit catches tampering despite guest claiming success',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m43-'));
  try{
    const fixture=setupFixture(root);
    const initial=hostAudit(fixture);
    assert.equal(initial.exact_file_allowlist,false);
    fs.writeFileSync(path.join(fixture.outside,'keep.txt'),'BAD');
    const tampered=hostAudit(fixture);
    assert.equal(tampered.outside_sentinel_unchanged,false);
    fs.writeFileSync(path.join(fixture.approved,'index.html'),'MALICIOUS');
    assert.equal(hostAudit(fixture).generated_html_matches,false);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('Windows platform admission rejects remote engines and overrides before guest launch',()=>{
  const mock=mockDocker({remote:true});
  const report=runWorkspace({platform:'win32',env:{},docker:mock.docker,name:NAME});
  assert.equal(report.status,'BLOCKED');
  assert.equal(mock.calls.filter(x=>x.args[0]==='run').length,0);
  const local=mockDocker();
  for(const entry of [
    {platform:'linux',env:{}},
    {platform:'darwin',env:{}},
    {platform:'win32',env:{DOCKER_HOST:'tcp://bad'}},
  ]){
    const result=runWorkspace({...entry,docker:local.docker,name:NAME});
    assert.equal(result.status,'BLOCKED');
  }
  assert.equal(local.calls.length,0);
});

test('no-argument CLI never launches Docker or creates a workspace',()=>{
  const proc=spawnSync(process.execPath,
    ['tools/m4/windows_scoped_workspace_qualification.mjs'],
    {encoding:'utf8',timeout:5000});
  assert.equal(proc.status,0,proc.stderr);
  assert.match(proc.stdout,/NOT_RUN/);
});

test('a guest failure blocks and cleans the disposable workspace',()=>{
  const mock=mockDocker({guestCode:2,cases:{...successful,system_write_denied:false},
    mutate:false});
  const r=runWorkspace({platform:'win32',env:{},docker:mock.docker,name:NAME});
  assert.equal(r.status,'BLOCKED');
  assert.equal(r.passedCases,CASES.length-1);
  assert.equal(r.temporaryFixtureRemoved,true);
  assert.equal(r.guestRemoved,true);
  assert.equal(r.generatedCodeExecutionEnabled,false);
  assert.equal(r.originalRepoMounted,false);
  assert.ok(r.reason.includes('system_write_denied'));
});

test('host sentinel tampering blocks regardless of guest Boolean results',()=>{
  const mock=mockDocker({siblingTamper:true});
  const r=runWorkspace({platform:'win32',env:{},docker:mock.docker,name:NAME});
  assert.equal(r.status,'BLOCKED');
  assert.equal(r.temporaryFixtureRemoved,true);
  assert.equal(r.guestRemoved,true);
  assert.equal(r.hostAudit?.outside_sentinel_unchanged,false);
  assert.equal(r.generatedCodeExecutionEnabled,false);
});

// Windows CI cannot safely claim real Docker qualification; Windows symlink
// creation may require developer-mode privileges. Test simulated positive
// end-to-end only on Linux, where the independent host audit can inspect it.
test('simulated positive disposable workspace completes both audits (Linux only)',
  {skip:process.platform==='win32'},
  ()=>{
    const mock=mockDocker();
    const r=runWorkspace({platform:'win32',env:{},docker:mock.docker,name:NAME});
    assert.equal(r.status,'SCOPED_FIXTURE_PASS',JSON.stringify(r));
    assert.equal(r.passedCases,17);
    assert.equal(r.hostPassed,9);
    assert.equal(r.guestRemoved,true);
    assert.equal(r.temporaryFixtureRemoved,true);
    assert.equal(r.generatedCodeExecutionEnabled,false);
    const run=mock.calls.find(x=>x.args[0]==='run');
    assert.equal(run.input,
      fs.readFileSync(new URL('../tools/m4/windows_scoped_workspace_guest.mjs',
        import.meta.url),'utf8'));
    assert.ok(!run.args.includes(run.input));
    assert.equal(mock.calls.filter(x=>x.args[0]==='rm').length,1);
  });
test('container ownership/cleanup failures cannot succeed',
  {skip:process.platform==='win32'},
  ()=>{
    for(const defect of [{wrongOwner:true},{containerLeft:true},
      {cleanupFailure:true}]){
      const mock=mockDocker(defect);
      const r=runWorkspace({platform:'win32',env:{},docker:mock.docker,name:NAME});
      assert.equal(r.status,'BLOCKED',JSON.stringify(defect));
      assert.equal(r.generatedCodeExecutionEnabled,false);
      assert.equal(r.temporaryFixtureRemoved,true);
    }
  });
