import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {prepareEvaluationCandidate} from '../tools/m4/static_web_evaluation_candidate.mjs';
import {
  parseTrustedStaticZip,dockerArgs,parseResult,smokeZip,
} from '../tools/m4/smoke_generated_zip.mjs';

const NAME='ca-smoke-012345abcdef';
const ID='sha256:'+'a'.repeat(64);
const DIGEST='mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64);
const meta={Id:ID,RepoDigests:[DIGEST],Os:'linux',Architecture:'amd64'};
const info={OSType:'linux',OperatingSystem:'Docker Desktop',
  PidsLimit:true,MemoryLimit:true};
const checks={
  exact_source_hashes:true,non_root:true,no_host_mounts:true,
  root_write_denied:true,network_denied:true,
  javascript_syntax:true,local_assets_linked:true,browser_rendered:true,
  source_integrity_unchanged:true,
};
function crc32(bytes){
  let crc=0xffffffff;
  for(const byte of bytes){
    crc^=byte;
    for(let n=0;n<8;n++)crc=crc&1?(crc>>>1)^0xedb88320:crc>>>1;
  }
  return (crc^0xffffffff)>>>0;
}
function buildZip(files,options={}){
  const chunks=[],central=[];
  let offset=0;
  for(const [name,content] of files){
    const filename=Buffer.from(name,'utf8');
    const data=Buffer.from(content,'utf8');
    const flags=options.descriptor?0x808:0x800;
    const h=Buffer.alloc(30);
    h.writeUInt32LE(0x04034b50,0);h.writeUInt16LE(20,4);
    h.writeUInt16LE(flags,6);h.writeUInt16LE(0,8);
    if(!options.descriptor){
      h.writeUInt32LE(crc32(data),14);
      h.writeUInt32LE(data.length,18);h.writeUInt32LE(data.length,22);
    }
    h.writeUInt16LE(filename.length,26);
    chunks.push(h,filename,data);
    const c=Buffer.alloc(46);
    c.writeUInt32LE(0x02014b50,0);
    c.writeUInt16LE(options.symlink?0x0314:20,4);
    c.writeUInt16LE(20,6);
    c.writeUInt16LE(flags,8);
    c.writeUInt32LE(crc32(data),16);
    c.writeUInt32LE(data.length,20);c.writeUInt32LE(data.length,24);
    c.writeUInt16LE(filename.length,28);
    c.writeUInt32LE(options.symlink?0xa1ff0000:0,38);
    c.writeUInt32LE(offset,42);
    central.push(c,filename);
    offset+=30+filename.length+data.length;
    if(options.descriptor){
      const descriptor=Buffer.alloc(16);
      descriptor.writeUInt32LE(0x08074b50,0);
      descriptor.writeUInt32LE(crc32(data),4);
      descriptor.writeUInt32LE(data.length,8);
      descriptor.writeUInt32LE(data.length,12);
      chunks.push(descriptor);offset+=16;
    }
  }
  const directory=Buffer.concat(central);
  const end=Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50,0);
  end.writeUInt16LE(files.length,8);
  end.writeUInt16LE(files.length,10);
  end.writeUInt32LE(directory.length,12);
  end.writeUInt32LE(offset,16);
  return Buffer.concat([...chunks,directory,end]);
}
const working=[
  ['index.html','<!doctype html><html><body><button>Compute</button><script src="./app.js"></script></body></html>'],
  ['app.js','document.querySelector("button").onclick=()=>{document.body.dataset.ok="yes"};'],
];
function fakeDocker(config={}){
  const calls=[];let ps=0;
  function docker(args,timeout,input){
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')
      return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context'&&args[1]==='inspect')
      return {status:0,stdout:JSON.stringify({Endpoints:{docker:{
        Host:config.remote?'tcp://otherhost:2375':'npipe:////./pipe/dockerDesktopLinuxEngine',
      }}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify(info)};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify(meta)};
    if(args[0]==='ps'){
      ps++;
      return {status:0,stdout:ps===1?'':(config.leftover?'id':'')};
    }
    if(args[0]==='run')return {
      status:config.exit??0,
      stdout:config.output??JSON.stringify({schemaVersion:1,
        checks:config.checks??checks,
        browserSandbox:'DISABLED_CONTAINER_ONLY'}),
      timedOut:config.timeout??false,stderr:'PRIVATE_SECRET',
    };
    if(args[0]==='container'&&args[1]==='inspect')
      return {status:config.absent?1:0,stdout:JSON.stringify({
        'codingassistant.source-smoke':config.wrongOwner?'WRONG':NAME,
      })};
    if(args[0]==='rm')return {status:0,stdout:''};
    throw Error('Unexpected Docker call '+args.join(' '));
  }
  return {docker,calls};
}
function withZip(files,func,options={}){
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),'ca-zip-test-'));
  const location=path.join(dir,'sample.zip');
  try{
    fs.writeFileSync(location,buildZip(files,options));
    return func(location);
  }finally{fs.rmSync(dir,{recursive:true,force:true});}
}
test('owner generated source ZIP is parsed without extraction onto the host',()=>{
  const zip=buildZip(working);
  const files=parseTrustedStaticZip(zip);
  assert.equal(files.length,2);
  assert.deepEqual(files.map(f=>f.path),['app.js','index.html']);
  assert.ok(files.every(f=>/^[0-9a-f]{64}$/.test(f.sha256)));
  assert.equal(files.find(f=>f.path==='app.js').content,working[1][1]);
  const descriptor=parseTrustedStaticZip(buildZip(working,{descriptor:true}));
  assert.deepEqual(descriptor,files);
});
test('unsafe paths, duplicates, extra file types, and symlink entries blocked',()=>{
  for(const files of [
    [['../evil.js','document.body.textContent="oops"'],working[0]],
    [['index.html','<html>valid</html>'],['INDEX.HTML','<html>clash</html>']],
    [working[0],['sneaky.sh','echo unsafe']],
    [working[0],['.env','PRIVATE_SECRET']],
    [working[0],['folder/../../evade.js','bad']],
    [working[1]],
    [working[0],['node_modules/a.js','evil']],
  ]){
    if(files===undefined)continue;
    assert.throws(()=>parseTrustedStaticZip(buildZip(files)),
      undefined,JSON.stringify(files.map(x=>x[0])));
  }
  assert.throws(()=>parseTrustedStaticZip(buildZip(working,{symlink:true})));
});
test('ZIP corruption, truncated and over-large archives never stage source',()=>{
  const good=buildZip(working);
  assert.throws(()=>parseTrustedStaticZip(Buffer.alloc(0)));
  assert.throws(()=>parseTrustedStaticZip(Buffer.alloc(3*1024*1024)));
  assert.throws(()=>parseTrustedStaticZip(good.subarray(0,good.length-7)));
  const altered=Buffer.from(good);
  const index=altered.indexOf('Compute');
  assert.ok(index>=0);
  altered[index]=0x78;
  assert.throws(()=>parseTrustedStaticZip(altered));
  const extra=Buffer.concat([good,Buffer.from('secret')]);
  assert.throws(()=>parseTrustedStaticZip(extra));
});
test('Docker host args keep no mount, no network, no caps and explicit risk flag',()=>{
  const args=dockerArgs(ID,NAME);
  for(const k of ['--rm','--pull=never','--network=none','--read-only',
    '--cap-drop=all','--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2','--init',
    '--run-owner-smoke','--input-type=module'])
    assert.ok(args.includes(k),k);
  for(const bad of ['--mount','-v','--volume','--privileged','--cap-add',
    '--network=host','--pid=host','--ipc=host','--device','--publish','-p'])
    assert.ok(!args.includes(bad),bad);
  assert.ok(!args.some(a=>a.startsWith('--pid=')));
  assert.throws(()=>dockerArgs('latest',NAME));
  assert.throws(()=>dockerArgs(ID,'invalid'));
  const guest=fs.readFileSync(
    new URL('../tools/m4/generated_zip_smoke_guest.mjs',import.meta.url),'utf8');
  assert.match(guest,/--no-sandbox/);
  assert.match(guest,/DISABLED_CONTAINER_ONLY/);
  assert.ok(!guest.includes('docker.sock:'));
  for(const file of ['tools/m4/generated_zip_smoke_guest.mjs',
    'tools/m4/smoke_generated_zip.mjs']){
    const check=spawnSync(process.execPath,['--check',file],{
      encoding:'utf8',timeout:4000,
    });
    assert.equal(check.status,0,check.stderr);
  }
});
test('exact report schema rejects forged VERIFIED and injected diagnostics',()=>{
  assert.deepEqual(parseResult(JSON.stringify({schemaVersion:1,
    checks,browserSandbox:'DISABLED_CONTAINER_ONLY'})),checks);
  for(const bad of [
    '{', '{}', '{"blocked":true}',
    JSON.stringify({schemaVersion:1,checks,
      browserSandbox:'ENABLED'}),
    JSON.stringify({schemaVersion:1,checks:{...checks,extra:true},
      browserSandbox:'DISABLED_CONTAINER_ONLY'}),
    JSON.stringify({schemaVersion:1,checks:{...checks,
      browser_rendered:'true'},browserSandbox:'DISABLED_CONTAINER_ONLY'}),
    JSON.stringify({schemaVersion:1,checks,
      browserSandbox:'DISABLED_CONTAINER_ONLY',status:'VERIFIED'}),
  ])assert.throws(()=>parseResult(bad));
});
test('single real-source ZIP smoke simulated success is never full verification',()=>{
  withZip(working,zip=>{
    const mocked=fakeDocker();
    const result=smokeZip({
      zip,name:NAME,platform:'win32',env:{},docker:mocked.docker});
    assert.equal(result.status,'SMOKE_PASS_NOT_VERIFIED',
      JSON.stringify(result));
    assert.equal(result.passed,9);
    assert.equal(result.containerRemoved,true);
    assert.equal(result.sourceFiles,2);
    assert.equal(result.generatedArtifactVerified,false);
    assert.equal(result.independentFunctionalVerification,false);
    assert.equal(result.browserSandbox,'DISABLED_CONTAINER_ONLY');
    const launches=mocked.calls.filter(x=>x.args[0]==='run');
    assert.equal(launches.length,1);
    assert.equal(mocked.calls.filter(x=>x.args[0]==='rm').length,1);
    assert.ok(launches[0].input.includes('const BASE64='));
    assert.ok(!launches[0].input.includes('__CA_UNTRUSTED_SNAPSHOT_BASE64__'));
    assert.ok(!launches[0].args.includes(launches[0].input));
    assert.ok(!mocked.calls.some(x=>x.args[0]==='pull'));
  });
});
test('failing source browser smoke produces NOT VERIFIED and useful failing code',()=>{
  withZip(working,zip=>{
    for(const key of ['javascript_syntax','local_assets_linked','browser_rendered']){
      const mocked=fakeDocker({exit:2,checks:{...checks,[key]:false}});
      const result=smokeZip({zip,name:NAME,platform:'win32',
        env:{},docker:mocked.docker});
      assert.equal(result.status,'SMOKE_TEST_FAILED');
      assert.equal(result.checks[key],false);
      assert.equal(result.generatedArtifactVerified,false);
      assert.equal(result.containerRemoved,true);
    }
  });
});
test('environmental failure, raw stderr, Docker crash and cleanup fail closed',()=>{
  withZip(working,zip=>{
    const scenarios=[
      {checks:{...checks,network_denied:false},exit:2},
      {exit:125,output:''},{timeout:true},{wrongOwner:true},
      {leftover:true},
    ];
    for(const scenario of scenarios){
      const mocked=fakeDocker(scenario);
      const report=smokeZip({zip,name:NAME,platform:'win32',
        env:{},docker:mocked.docker});
      assert.equal(report.status,'BLOCKED',JSON.stringify(scenario));
      assert.equal(report.generatedArtifactVerified,false);
      assert.ok(!String(report.reason).includes('PRIVATE_SECRET'));
    }
  });
});
test('host admission rejects remote Docker and unsupported OS before execution',()=>{
  withZip(working,zip=>{
    const remote=fakeDocker({remote:true});
    const result=smokeZip({zip,name:NAME,platform:'win32',
      env:{},docker:remote.docker});
    assert.equal(result.status,'BLOCKED');
    assert.equal(remote.calls.filter(x=>x.args[0]==='run').length,0);
    const invalid=fakeDocker();
    for(const opts of [
      {platform:'linux',env:{}},{platform:'darwin',env:{}},
      {platform:'win32',env:{DOCKER_HOST:'tcp://remote'}},
    ])assert.equal(smokeZip({
      zip,name:NAME,docker:invalid.docker,...opts}).status,'BLOCKED');
    assert.equal(invalid.calls.length,0);
  });
});
test('running without explicit ZIP never touches Docker',()=>{
  const check=spawnSync(process.execPath,['tools/m4/smoke_generated_zip.mjs'],{
    encoding:'utf8',timeout:5000});
  assert.equal(check.status,0,check.stderr);
  assert.match(check.stdout,/NOT_RUN/);
});


test('internal API consumes JSON directly without user-facing ZIP artifact handoff',()=>{
  const result=spawnSync(process.execPath,[
    'tools/m4/smoke_generated_zip.mjs','--zip',
    path.join(os.tmpdir(),'ca-definitely-does-not-exist.zip'),'--json',
  ],{encoding:'utf8',timeout:5000});
  assert.equal(result.status,2);
  const parsed=JSON.parse(result.stdout.trim());
  assert.equal(parsed.status,'BLOCKED');
  assert.equal(parsed.generatedArtifactVerified,false);
  assert.equal(parsed.browserSandbox,'DISABLED_CONTAINER_ONLY');
  assert.equal(parsed.required,9);
  assert.ok(!result.stdout.includes('Evidence:'));
  assert.equal(result.stderr,'');
});

function evaluationScope(archive){
  const hash=x=>createHash('sha256').update(x).digest('hex');
  const parsed=parseTrustedStaticZip(archive);
  const files=parsed.map(f=>({path:f.path,sha256:f.sha256,utf8Bytes:Buffer.byteLength(f.content)}))
    .sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  const sourceFingerprint=hash([...files].sort((a,b)=>a.path.toUpperCase()<b.path.toUpperCase()?-1:a.path.toUpperCase()>b.path.toUpperCase()?1:0)
    .map(f=>f.path+'\t'+f.sha256).join('\n'));
  return {schemaVersion:1,status:'SCOPE_CANDIDATE',executionEnabled:false,
    verificationStatus:'GENERATED_NOT_TESTED',blockers:[],taskId:'a'.repeat(32),
    engineeringRunId:'b'.repeat(32),planRevision:1,planSha256:'c'.repeat(64),
    specificationVersion:1,specificationSha256:'d'.repeat(64),architectureSha256:'e'.repeat(64),
    model:'qwen2.5-coder:14b',scopeSha256:'f'.repeat(64),sourceFingerprint,files};
}
test('exact approved-scope candidate binds archive, evaluator and mandatory cases without execution authority',()=>{
  const archive=buildZip(working),currentScope=evaluationScope(archive);
  const selection={...currentScope};
  const candidate=prepareEvaluationCandidate({currentScope,selection,archive});
  assert.equal(candidate.status,'CANDIDATE_ONLY');
  assert.equal(candidate.executionEnabled,false);
  assert.equal(candidate.runtimeQualified,false);
  assert.equal(candidate.generatedArtifactVerified,false);
  assert.equal(candidate.checks.length,7);
  assert.equal(candidate.files.length,2);
  assert.ok(Object.isFrozen(candidate.files[0]));
  assert.match(candidate.evaluatorSha256,/^[a-f0-9]{64}$/);
  assert.match(candidate.descriptorSha256,/^[a-f0-9]{64}$/);
  assert.equal(candidate.descriptorSha256,prepareEvaluationCandidate({currentScope,selection,archive}).descriptorSha256);
  assert.ok(!JSON.stringify(candidate).includes('document.querySelector'));
});
test('stale task/run/specification/plan/model/source selections and changed archives fail before staging',()=>{
  const archive=buildZip(working),currentScope=evaluationScope(archive);
  for(const [key,value] of Object.entries({taskId:'9'.repeat(32),engineeringRunId:'9'.repeat(32),planRevision:2,
    planSha256:'9'.repeat(64),specificationVersion:2,specificationSha256:'9'.repeat(64),architectureSha256:'9'.repeat(64),
    model:'other-model',scopeSha256:'9'.repeat(64),sourceFingerprint:'9'.repeat(64)})){
    assert.throws(()=>prepareEvaluationCandidate({currentScope,selection:{...currentScope,[key]:value},archive}),/SCOPE_NOT_CURRENT/,key);
  }
  for(const mutated of [{status:'BLOCKED'},{executionEnabled:true},{blockers:['STALE']},{verificationStatus:'VERIFIED'}]){
    const changed={...currentScope,...mutated};
    assert.throws(()=>prepareEvaluationCandidate({currentScope:changed,selection:changed,archive}),/SCOPE_NOT_CURRENT/);
  }
  const changed=buildZip([working[0],['app.js','document.body.textContent="different";']]);
  assert.throws(()=>prepareEvaluationCandidate({currentScope,selection:currentScope,archive:changed}),/MANIFEST_MISMATCH/);
  for(const unsafe of [buildZip([working[0],['../escape.js','unsafe']]),buildZip(working,{symlink:true}),buildZip([...working,['extra.js','extra']])]){
    assert.throws(()=>prepareEvaluationCandidate({currentScope,selection:currentScope,archive:unsafe}));
  }
  const wrongFingerprint={...currentScope,sourceFingerprint:'9'.repeat(64)};
  assert.throws(()=>prepareEvaluationCandidate({currentScope:wrongFingerprint,selection:wrongFingerprint,archive}),/FINGERPRINT_MISMATCH/);
});
