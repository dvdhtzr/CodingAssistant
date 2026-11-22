import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {
  readPinnedSources,gitBlobHash,prepareSnapshot,verifySnapshot,
  dockerArgs,parseEvaluation,auditSnapshot,runQualification,main,
} from '../tools/m4/windows_calculator_evaluator_qualification.mjs';
import {FILES,CASES} from '../tools/m4/windows_calculator_evaluator_guest.mjs';

const IMAGE='sha256:'+'a'.repeat(64);
const DIGEST='mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64);
const NAME='ca-win-m44-012345abcdef';
const INFO={OSType:'linux',OperatingSystem:'Docker Desktop',
  PidsLimit:true,MemoryLimit:true};
const IMAGE_META={Id:IMAGE,RepoDigests:[DIGEST],Os:'linux',Architecture:'amd64'};
const good=Object.fromEntries(CASES.map(k=>[k,true]));
function ok(status=0,stdout=''){return {status,stdout};}
function fakeDocker(change={}){
  const calls=[];let psCalls=0;
  function docker(args,timeout,input){
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')return ok(0,'desktop-linux');
    if(args[0]==='context'&&args[1]==='inspect')
      return ok(0,JSON.stringify({Endpoints:{docker:{
        Host:change.remote?'tcp://remote:2375':
          'npipe:////./pipe/dockerDesktopLinuxEngine',
      }}}));
    if(args[0]==='info')return ok(0,JSON.stringify(INFO));
    if(args[0]==='image')return ok(change.imageMissing?1:0,JSON.stringify(IMAGE_META));
    if(args[0]==='ps'){
      psCalls++;
      return psCalls===1?
        ok(change.collision?0:0,change.collision?'old-container':''):
        ok(change.cleanupFailure?1:0,change.containerLeft?'leftover':'');
    }
    if(args[0]==='run'){
      if(change.modifySnapshot){
        const mount=args[args.indexOf('--mount')+1];
        const source=mount.slice('type=bind,source='.length).split(',target=/workspace')[0];
        fs.writeFileSync(path.join(source,'working.js'),'MALICIOUS');
      }
      if(change.modifyNeighbor){
        const mount=args[args.indexOf('--mount')+1];
        const source=mount.slice('type=bind,source='.length).split(',target=/workspace')[0];
        fs.writeFileSync(path.join(path.dirname(source),'neighbor','keep.txt'),'BAD');
      }
      return {status:change.status??0,stdout:change.output??JSON.stringify(
        change.tests??good),timedOut:change.timeout??false};
    }
    if(args[0]==='container'&&args[1]==='inspect')
      return ok(change.noContainer?1:0,JSON.stringify({
        'codingassistant.m44':change.wrongOwner?'unrelated':NAME,
      }));
    if(args[0]==='rm')return ok();
    throw Error('unexpected Docker op '+args.join(' '));
  }
  return {docker,calls};
}

test('trusted calculator fixtures are exact pinned blobs, never model text',()=>{
  assert.equal(FILES.length,4);
  assert.equal(CASES.length,16);
  assert.equal(new Set(CASES).size,16);
  const source=readPinnedSources();
  assert.deepEqual(Object.keys(source),FILES);
  for(const file of FILES){
    assert.ok(Buffer.isBuffer(source[file]));
    assert.match(gitBlobHash(source[file]),/^[0-9a-f]{40}$/);
  }
  assert.ok(source['broken.html'].toString().includes('TODO connect calculator'));
  assert.ok(source['working.html'].toString().includes('working.js'));
  assert.ok(source['working.js'].toString().includes('localStorage'));
});

test('fixed Chrome guest and independent browser harness are syntax checked',()=>{
  const guest=fs.readFileSync(new URL(
    '../tools/m4/windows_calculator_evaluator_guest.mjs',import.meta.url),'utf8');
  const harness=fs.readFileSync(new URL(
    '../tools/m4/windows_calculator_harness.js',import.meta.url),'utf8');
  for(const file of ['tools/m4/windows_calculator_evaluator_guest.mjs',
    'tools/m4/windows_calculator_harness.js',
    'tools/m4/windows_calculator_evaluator_qualification.mjs']){
    const result=spawnSync(process.execPath,['--check',file],
      {encoding:'utf8',timeout:5000});
    assert.equal(result.status,0,file+' '+result.stderr);
  }
  for(const caseName of [
    'negative_inert_browser','mouse_2_plus_3_equals_5',
    'keyboard_12_5_divided_by_2','decimal_0_1_plus_0_2',
    'back_and_history_recall','history_survives_browser_restart',
  ])assert.ok(guest.includes(caseName)||harness.includes(caseName),caseName);
  assert.ok(guest.includes('--no-sandbox')); // TRUSTED fixture ONLY
  assert.ok(!guest.includes('github.com'));
  assert.ok(!guest.includes('npm install'));
  assert.ok(!guest.includes('git push'));
  assert.ok(!harness.includes('fetch('));
});

test('temporary snapshot uses immutable exact fixtures and read-only mount',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m44-'));
  try{
    const fixture=prepareSnapshot(root,readPinnedSources());
    assert.equal(verifySnapshot(fixture.snapshot),fs.realpathSync(fixture.snapshot));
    const argv=dockerArgs(IMAGE,NAME,fixture.snapshot);
    for(const goodFlag of [
      '--pull=never','--network=none','--read-only','--cap-drop=all',
      '--security-opt=no-new-privileges','--user=1000:1000',
      '--pids-limit=96','--memory=2g','--cpus=2',
      '--mount','--input-type=module','-','--run-trusted-evaluator',
    ])assert.ok(argv.includes(goodFlag),'missing '+goodFlag);
    const mounts=argv.filter(x=>x==='--mount');
    assert.equal(mounts.length,1);
    assert.equal(argv[argv.indexOf('--mount')+1],
      'type=bind,source='+fs.realpathSync(fixture.snapshot)+
      ',target=/workspace,readonly,bind-propagation=rprivate');
    for(const forbidden of [
      '--privileged','--network=host','--pid=host','--cap-add',
      '--publish','--env-file','--volume','-v','--device',
    ])assert.ok(!argv.includes(forbidden),forbidden);
    assert.ok(!argv.some(arg=>arg.startsWith('--pid=')));
    assert.throws(()=>dockerArgs('latest',NAME,fixture.snapshot));
    assert.throws(()=>dockerArgs(IMAGE,'unrelated',fixture.snapshot));
    assert.deepEqual(Object.values(auditSnapshot(fixture)),[true,true,true,true]);
    fs.writeFileSync(path.join(fixture.snapshot,'working.js'),'evil');
    assert.throws(()=>verifySnapshot(fixture.snapshot));
    assert.equal(auditSnapshot(fixture).exact_snapshot_unchanged,false);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

test('snapshot denies extra paths and non-temporary folders',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m44-'));
  try{
    const fixture=prepareSnapshot(root,readPinnedSources());
    fs.writeFileSync(path.join(fixture.snapshot,'extra.txt'),'not approved');
    assert.throws(()=>verifySnapshot(fixture.snapshot));
  }finally{fs.rmSync(root,{recursive:true,force:true});}
  const unrelated=fs.mkdtempSync(path.join(os.tmpdir(),'other-m44-'));
  try{
    const fixture=prepareSnapshot(unrelated,readPinnedSources());
    assert.throws(()=>verifySnapshot(fixture.snapshot));
  }finally{fs.rmSync(unrelated,{recursive:true,force:true});}
});

test('exact 16 Boolean test results are necessary; failed negative case blocks',()=>{
  assert.deepEqual(parseEvaluation(JSON.stringify(good)),good);
  for(const invalid of [
    '','[]','{}','{"blocked":true}',
    JSON.stringify({...good,extra:true}),
    JSON.stringify({...good,[CASES[0]]:'true'}),
    JSON.stringify(Object.fromEntries(Object.entries(good).slice(1))),
    'x'.repeat(5000),
  ])assert.throws(()=>parseEvaluation(invalid));
  const failures={...good,negative_inert_browser:false};
  assert.equal(parseEvaluation(JSON.stringify(failures)).negative_inert_browser,false);
});

test('simulated Windows success requires both independent guest and host audit',()=>{
  const mock=fakeDocker();
  const report=runQualification({platform:'win32',env:{},docker:mock.docker,name:NAME});
  assert.equal(report.status,'TRUSTED_REFERENCE_EVALUATOR_PASS',
    JSON.stringify(report));
  assert.equal(report.passedTests,16);
  assert.equal(report.hostPassed,4);
  assert.equal(report.containerRemoved,true);
  assert.equal(report.temporarySnapshotRemoved,true);
  assert.equal(report.verificationStatus,'TRUSTED_FIXTURE_ONLY');
  for(const key of [
    'modelInvoked','generatedSourceExecuted','untrustedBrowserQualified',
    'staticWebRuntimeQualified','generatedCodeExecutionEnabled',
  ])assert.equal(report[key],false);
  assert.equal(mock.calls.filter(x=>x.args[0]==='run').length,1);
  assert.equal(mock.calls.filter(x=>x.args[0]==='rm').length,1);
  const run=mock.calls.find(x=>x.args[0]==='run');
  assert.ok(run.input.includes('history_survives_browser_restart'));
  assert.ok(!run.input.includes('PLACEHOLDER_M44_HARNESS_SOURCE'));
  assert.ok(!run.args.includes(run.input));
  assert.ok(!mock.calls.some(x=>x.args[0]==='pull'||x.args[0]==='build'));
});

test('failed browser, negative control, mutated snapshot or neighbor fails closed',()=>{
  const scenarios=[
    {tests:{...good,mouse_2_plus_3_equals_5:false},status:2},
    {tests:{...good,negative_inert_browser:false},status:2},
    {modifySnapshot:true},
    {modifyNeighbor:true},
    {status:125,output:'',timeout:false},
    {timeout:true},
    {wrongOwner:true},
    {containerLeft:true},
    {cleanupFailure:true},
  ];
  for(const changes of scenarios){
    const mock=fakeDocker(changes);
    const report=runQualification({
      platform:'win32',env:{},docker:mock.docker,name:NAME,
    });
    assert.equal(report.status,'BLOCKED',JSON.stringify(changes));
    assert.equal(report.staticWebRuntimeQualified,false);
    assert.equal(report.generatedCodeExecutionEnabled,false);
    assert.equal(report.temporarySnapshotRemoved,true);
    if(changes.wrongOwner||changes.cleanupFailure||changes.containerLeft)
      assert.equal(report.containerRemoved,false);
  }
});

test('remote Docker, absent image, name collision or host override never runs evaluator',()=>{
  for(const changes of [
    {remote:true},{imageMissing:true},{collision:true},
  ]){
    const mock=fakeDocker(changes);
    const r=runQualification({platform:'win32',env:{},docker:mock.docker,name:NAME});
    assert.equal(r.status,'BLOCKED');
    assert.equal(mock.calls.filter(x=>x.args[0]==='run').length,0);
  }
  const mock=fakeDocker();
  for(const options of [
    {platform:'linux',env:{}},
    {platform:'darwin',env:{}},
    {platform:'win32',env:{DOCKER_HOST:'tcp://bad'}},
  ]){
    const report=runQualification({...options,docker:mock.docker,name:NAME});
    assert.equal(report.status,'BLOCKED');
  }
  assert.equal(mock.calls.length,0);
});

test('without --run the CLI must never invoke Docker',()=>{
  const p=spawnSync(process.execPath,
    ['tools/m4/windows_calculator_evaluator_qualification.mjs'],
    {encoding:'utf8',timeout:5000});
  assert.equal(p.status,0,p.stderr);
  assert.match(p.stdout,/NOT_RUN/);
});
