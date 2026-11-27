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

import {prepareEvaluationAdmission,acknowledgeEvaluationPlan,bindTrustedFixtureReceipt}
  from '../tools/m4/static_web_evaluation_admission.mjs';
import {qualifyTrusted} from '../tools/m4/windows_aet8_trusted_qualification.mjs';
import {SECURITY_CASES,FIXTURE_CASES} from '../tools/m4/windows_aet8_trusted_guest.mjs';
import {CALCULATION_CASES,CALCULATION_CONTRACT_VERSION} from '../tools/m4/calculation_journal_contract.mjs';
const allCases=(keys,value)=>Object.fromEntries(keys.map(k=>[k,value]));
function admissionFixture(){
  const archive=buildZip(working),currentScope=evaluationScope(archive);
  const image={Id:'sha256:'+'a'.repeat(64),RepoDigests:['mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64)],
    Os:'linux',Architecture:'amd64'};
  const name='ca-win-aet8-012345abcdef';
  const receipt=qualifyTrusted({platform:'win32',env:{},name,docker(args){
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify({OSType:'linux',OperatingSystem:'Docker Desktop',PidsLimit:true,MemoryLimit:true})};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify(image)};
    if(args[0]==='ps')return {status:0,stdout:''};
    if(args[0]==='run')return {status:0,stdout:JSON.stringify({schemaVersion:1,
      security:allCases(SECURITY_CASES,true),fixture:allCases(FIXTURE_CASES,true),
      observations:{working:allCases(CALCULATION_CASES,true),broken:allCases(CALCULATION_CASES,false),spoof:allCases(CALCULATION_CASES,false)}})};
    if(args[0]==='container')return {status:0,stdout:JSON.stringify({'codingassistant.aet8':name})};
    if(args[0]==='rm')return {status:0,stdout:''};
    throw Error('Unexpected command');
  }});
  assert.equal(receipt.status,'TRUSTED_INTEGRATED_EVALUATOR_PASS');
  const criteria=CALCULATION_CASES.map((c,i)=>({id:'criterion-'+i,text:c}));
  const reviewedContract={contractId:CALCULATION_CONTRACT_VERSION,planSha256:currentScope.planSha256,
    specificationSha256:currentScope.specificationSha256,architectureSha256:currentScope.architectureSha256,
    mappings:criteria.map(c=>({criterionId:c.id,criterionText:c.text,cases:[c.text]}))};
  return {archive,currentScope,selection:currentScope,criteria,reviewedContract,
    qualificationReport:receipt,currentImageMetadata:image};
}
test('exact functional evaluation admission remains non-executable even after explicit planning acknowledgement',()=>{
  const inputs=admissionFixture();
  const preview=prepareEvaluationAdmission(inputs);
  assert.equal(preview.status,'ADMISSION_PREVIEW_ONLY');
  assert.equal(preview.mappings.length,7);
  assert.equal(preview.qualification.hostProvenanceEstablished,false);
  assert.equal(preview.executionEnabled,false);
  assert.equal(preview.runtimeQualified,false);
  assert.equal(preview.generatedArtifactVerified,false);
  assert.equal(preview.admissionSha256,prepareEvaluationAdmission(inputs).admissionSha256);
  assert.ok(Object.isFrozen(preview.mappings[0].cases));
  const request={admissionSha256:preview.admissionSha256,confirmation:'REVIEW EVALUATION '+preview.admissionSha256};
  const ack=acknowledgeEvaluationPlan(inputs,request);
  assert.equal(ack.status,'PLAN_REVIEWED_NON_EXECUTABLE');
  assert.equal(ack.executionEnabled,false);
  assert.throws(()=>acknowledgeEvaluationPlan(inputs,{...request,confirmation:'EXECUTE '+preview.admissionSha256}));
  assert.throws(()=>acknowledgeEvaluationPlan(inputs,{...request,executionEnabled:true}));
  assert.throws(()=>acknowledgeEvaluationPlan({...inputs,qualificationReport:{...inputs.qualificationReport,
    recordedAtUtc:'2026-11-26T00:00:00.000Z'}},request),/CONFIRMATION_REQUIRED/);
});
test('admission rejects incomplete, spoofed and stale qualification rather than trusting the pass string',()=>{
  const inputs=admissionFixture(),receipt=inputs.qualificationReport;
  for(const mutated of [
    {...receipt,containerRemoved:false},{...receipt,runtimeQualified:true},{...receipt,generatedArtifactVerified:true},
    {...receipt,driverSha256:'1'.repeat(64)},{...receipt,seccompSha256:'2'.repeat(64)},
    {...receipt,guestSha256:'3'.repeat(64)},{...receipt,trustedSourceHashes:{}},
    {...receipt,security:{...receipt.security,[SECURITY_CASES[0]]:false}},
    {...receipt,observations:{...receipt.observations,spoof:{...receipt.observations.spoof,[CALCULATION_CASES[0]]:true}}},
    {...receipt,observations:{...receipt.observations,broken:null}},
    {...receipt,extra:'VERIFIED'}
  ])assert.throws(()=>bindTrustedFixtureReceipt(mutated,inputs.currentImageMetadata));
  assert.throws(()=>bindTrustedFixtureReceipt(receipt,{...inputs.currentImageMetadata,Id:'sha256:'+'c'.repeat(64)}));
});
test('admission never substitutes a fixed calculator contract for unrelated or uncovered approved requirements',()=>{
  const inputs=admissionFixture(),contract=inputs.reviewedContract;
  assert.throws(()=>prepareEvaluationAdmission({...inputs,reviewedContract:null}),/CONTRACT_NOT_CURRENT/);
  assert.throws(()=>prepareEvaluationAdmission({...inputs,criteria:[{id:'ideas',text:'Users can edit and delete ideas'}]}));
  for(const mappings of [contract.mappings.slice(1),[...contract.mappings.slice(1),contract.mappings[1]],
    contract.mappings.map((m,i)=>i===0?{...m,cases:['not-a-reviewed-case']}:m),
    contract.mappings.map((m,i)=>i===0?{...m,criterionText:'different approved requirement'}:m),
    contract.mappings.map(m=>({...m,cases:[CALCULATION_CASES[0]]}))])
    assert.throws(()=>prepareEvaluationAdmission({...inputs,reviewedContract:{...contract,mappings}}));
  assert.throws(()=>prepareEvaluationAdmission({...inputs,reviewedContract:{...contract,planSha256:'0'.repeat(64)}}));
});

import {prepareLocalEvaluation,readLocalImage,pinnedMetadataDocker} from '../tools/m4/static_web_evaluation_host_bridge.mjs';
function planningBridgeFixture(){
  const inputs=admissionFixture(),calls=[];
  const name='windows-static-web-012345abcdef.json';
  const request={operation:'prepare-admission',receiptName:name,currentScope:inputs.currentScope,
    archiveBase64:inputs.archive.toString('base64'),criteria:inputs.criteria,reviewedContract:inputs.reviewedContract};
  const options={platform:'win32',env:{},readReceipt(receipt){assert.equal(receipt,name);return inputs.qualificationReport;},
    docker(args){calls.push(args);
      if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
      if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
      if(args[0]==='info')return {status:0,stdout:JSON.stringify({OSType:'linux',OperatingSystem:'Docker Desktop',PidsLimit:true,MemoryLimit:true})};
      if(args[0]==='image')return {status:0,stdout:JSON.stringify(inputs.currentImageMetadata)};
      throw Error('Unexpected Docker command');
    }};
  return {inputs,request,options,calls,name};
}
test('local host bridge reads qualification and metadata without launching a container or candidate',()=>{
  const f=planningBridgeFixture();
  const preview=prepareLocalEvaluation(f.request,f.options);
  assert.equal(preview.admissionSha256,prepareEvaluationAdmission(f.inputs).admissionSha256);
  assert.equal(preview.executionEnabled,false);
  assert.equal(preview.qualification.hostProvenanceEstablished,false);
  assert.deepEqual(f.calls.map(a=>a.slice(0,2)),[['context','show'],['context','inspect'],['info','--format'],['image','inspect']]);
  const inspected=prepareLocalEvaluation({operation:'inspect-receipt',receiptName:f.name},f.options);
  assert.equal(inspected.status,'TRUSTED_FIXTURE_BINDING_INSPECTED');
  assert.equal(inspected.runtimeQualified,false);
});
test('host bridge rejects arbitrary paths, forged metadata, remote endpoints and extra operations',()=>{
  const f=planningBridgeFixture();
  for(const request of [{...f.request,receiptName:'../secret.json'},{...f.request,qualificationReport:f.inputs.qualificationReport},
    {...f.request,currentImageMetadata:f.inputs.currentImageMetadata},{...f.request,operation:'execute'},
    {...f.request,archiveBase64:'invalid!!'}])
    assert.throws(()=>prepareLocalEvaluation(request,f.options));
  assert.equal(f.calls.length,0);
  assert.throws(()=>readLocalImage({...f.options,env:{DOCKER_HOST:'tcp://remote'}}));
  const remote={...f.options,docker(args){
    if(args[0]==='context'&&args[1]==='inspect')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'tcp://remote'}}})};
    return f.options.docker(args);
  }};
  assert.throws(()=>readLocalImage(remote),/EVALUATION_DOCKER_BINDING_INVALID/);
  assert.ok(!f.calls.some(a=>a[0]==='run'||a[0]==='pull'));
});
test('host bridge CLI without explicit input is inert',()=>{
  const result=spawnSync(process.execPath,['tools/m4/static_web_evaluation_host_bridge.mjs'],{encoding:'utf8',timeout:5000});
  assert.equal(result.status,0,result.stderr);assert.match(result.stdout,/NOT_RUN/);
});

test('stdin metadata Docker requires an explicit local executable and digest',()=>{
  for(const env of [{},{CA_EVALUATION_DOCKER_EXE:'docker.exe',CA_EVALUATION_DOCKER_SHA256:'a'.repeat(64)},
    {CA_EVALUATION_DOCKER_EXE:'C:/Docker/docker.exe',CA_EVALUATION_DOCKER_SHA256:'bad'},
    {CA_EVALUATION_DOCKER_EXE:'C:/Docker/other.exe',CA_EVALUATION_DOCKER_SHA256:'a'.repeat(64)}])
    assert.throws(()=>pinnedMetadataDocker(env,'win32'),/EVALUATION_DOCKER_PROVENANCE_REQUIRED/);
  assert.throws(()=>pinnedMetadataDocker({CA_EVALUATION_DOCKER_EXE:'C:/Docker/docker.exe',
    CA_EVALUATION_DOCKER_SHA256:'a'.repeat(64)},'linux'),/EVALUATION_DOCKER_PROVENANCE_REQUIRED/);
});

import {prepareCandidatePayload} from '../tools/m4/static_web_candidate_payload.mjs';
import {createCandidateAssets} from '../tools/m4/static_web_candidate_assets.mjs';
import {parseCandidateObservations} from '../tools/m4/static_web_candidate_observations.mjs';
test('candidate payload binds reviewed admission and serves only exact in-memory assets',()=>{
  const input=admissionFixture(),admission=prepareEvaluationAdmission(input);
  const staged=prepareCandidatePayload(input,admission.admissionSha256);
  assert.equal(staged.status,'CANDIDATE_PAYLOAD_ONLY');
  assert.equal(staged.executionEnabled,false);
  const assets=createCandidateAssets(staged.payloadJson,staged.binding);
  assert.equal(assets.entryPath,'/index.html');
  assert.equal(assets.resolve('GET','/index.html').body,working[0][1]);
  assert.equal(assets.resolve('GET','/app.js').contentType,'application/javascript; charset=utf-8');
  for(const target of ['/','/INDEX.html','/../index.html','/%69ndex.html','/index.html?x=1','/app.js#x','/etc/passwd','/missing.js'])
    assert.equal(assets.resolve('GET',target).status,404,target);
  assert.equal(assets.resolve('POST','/index.html').status,404);
  assert.ok(Object.isFrozen(assets.resolve('GET','/index.html')));
});
test('candidate payload rejects changed approval, tampered bytes, omitted assets and extra fields',()=>{
  const input=admissionFixture(),admission=prepareEvaluationAdmission(input);
  assert.throws(()=>prepareCandidatePayload(input,'0'.repeat(64)),/ADMISSION_CHANGED/);
  const staged=prepareCandidatePayload(input,admission.admissionSha256);
  const payload=JSON.parse(staged.payloadJson);
  for(const mutate of [
    p=>p.files[0].content+='changed',p=>p.files.pop(),p=>p.files.push(p.files[0]),
    p=>p.files[0].path='../index.html',p=>p.files[0].executionEnabled=true,
    p=>p.candidateSha256='0'.repeat(64),p=>p.executionEnabled=true]){
    const copy=structuredClone(payload);mutate(copy);
    assert.throws(()=>createCandidateAssets(JSON.stringify(copy),staged.binding));
  }
});
function candidateObservationFixture(){
  const expected={attemptId:'1'.repeat(32),candidateSha256:'2'.repeat(64),archiveSha256:'3'.repeat(64),
    evaluatorSha256:'4'.repeat(64),adapterSha256:'5'.repeat(64),policySha256:'6'.repeat(64)};
  const report={schemaVersion:1,...expected,security:allCases(SECURITY_CASES,true),
    build:allCases(['exact_source_hashes','javascript_syntax','local_assets_linked','source_integrity_unchanged'],true),
    infrastructure:allCases(['browser_sandbox_confirmed','independent_driver_confirmed','browser_closed'],true),
    cases:allCases(CALCULATION_CASES,true)};
  return {expected,report};
}
test('candidate observations distinguish complete functional failures from infrastructure blockers',()=>{
  const {expected,report}=candidateObservationFixture();
  const read=r=>parseCandidateObservations(JSON.stringify(r),expected);
  assert.equal(read(report).status,'CHECKS_PASSED_NOT_VERIFIED');
  assert.equal(read(report).generatedArtifactVerified,false);
  const failed={...report,cases:{...report.cases,[CALCULATION_CASES[2]]:false}};
  assert.equal(read(failed).status,'TESTS_FAILED');
  assert.equal(read(failed).passed,6);
  assert.equal(read({...failed,infrastructure:{...report.infrastructure,browser_closed:false}}).status,'VERIFICATION_BLOCKED');
  assert.equal(read({...failed,cases:{...failed.cases,[CALCULATION_CASES[0]]:null}}).status,'VERIFICATION_BLOCKED');
  assert.equal(read({...report,build:{...report.build,javascript_syntax:false},cases:allCases(CALCULATION_CASES,null)}).status,'BUILD_FAILED');
});
test('candidate observations reject forged success, stale identity and contradictory security/build results',()=>{
  const {expected,report}=candidateObservationFixture();
  for(const bad of [
    {...report,status:'VERIFIED'},{...report,attemptId:'0'.repeat(32)},
    {...report,policySha256:'0'.repeat(64)},{...report,generatedArtifactVerified:true},
    {...report,security:{...report.security,[SECURITY_CASES[0]]:false}},
    {...report,build:{...report.build,javascript_syntax:false}},
    {...report,cases:{...report.cases,unknown:true}},
    {...report,cases:{...report.cases,[CALCULATION_CASES[0]]:'true'}}])
    assert.throws(()=>parseCandidateObservations(JSON.stringify(bad),expected),/OBSERVATIONS_INVALID/);
});

import {createCandidatePage,createCandidateSessionManager} from '../tools/m4/static_web_candidate_driver.mjs';
test('candidate driver uses isolated world DOM observations and fixed locator vocabulary',async()=>{
  const calls=[],origin='http://127.0.0.1:12345',url=origin+'/index.html';
  let frameUrl=url,active=true;
  const transport={async send(method,params,sessionId){
    calls.push({method,params,sessionId});
    if(method==='Page.getFrameTree')return {frameTree:{frame:{id:'frame',url:frameUrl}}};
    if(method==='Page.createIsolatedWorld')return {executionContextId:42};
    if(method==='Runtime.evaluate')return {result:{value:'5'}};
    throw Error('unexpected CDP command');
  }};
  const {page}=createCandidatePage(transport,'session',origin,()=>{if(!active)throw Error('STALE');});
  assert.equal(await page.locator('output#display').innerText(),'5');
  assert.deepEqual(calls.map(c=>c.method),['Page.getFrameTree','Page.createIsolatedWorld','Runtime.evaluate']);
  assert.equal(calls[2].params.contextId,42);
  assert.equal(calls[1].params.worldName,'ca-candidate-independent-v1');
  assert.ok(!Object.hasOwn(calls[1].params,'grantUniveralAccess'));
  assert.throws(()=>page.locator('body'),/NOT_REVIEWED/);
  assert.throws(()=>page.getByRole('button',{name:'unreviewed',exact:true}),/NOT_REVIEWED/);
  await assert.rejects(()=>page.keyboard.type('arbitrary'),/NOT_REVIEWED/);
  frameUrl='http://127.0.0.1:12345/other.html';
  await assert.rejects(()=>page.locator('output#display').innerText(),/NAVIGATION_CHANGED/);
  active=false;
  await assert.rejects(()=>page.locator('output#display').innerText(),/STALE/);
  assert.throws(()=>createCandidatePage(transport,'session','https://example.org'),/ORIGIN_INVALID/);
});
function candidateLifecycleFixture({closeFails=false}={}){
  const calls=[];let profiles=0,processes=0;
  const driver=createCandidateSessionManager('http://127.0.0.1:12345',{
    createProfile:async()=>{const profile=++profiles;calls.push(['profile',profile]);return profile;},
    async launch(profile,origin,assertActive){
      const pid=++processes;calls.push(['launch',profile,pid]);
      return {page:{pid,read(){assertActive();return pid;}},
        async navigate(){assertActive();calls.push(['navigate',pid]);},
        async close(){calls.push(['close',pid]);if(closeFails)throw Error('SHUTDOWN_FAILED');}};
    },
    removeProfile:async profile=>calls.push(['remove',profile])
  });
  return {driver,calls};
}
test('candidate cases use new profiles and cold restart preserves only the current case profile',async()=>{
  const {driver,calls}=candidateLifecycleFixture();
  const first=await driver.openFresh();
  assert.equal(first.page.read(),1);
  await assert.rejects(()=>driver.openFresh(),/SESSION_BUSY/);
  const restarted=await first.restart();
  assert.equal(restarted.read(),2);
  assert.throws(()=>first.page.read(),/SESSION_STALE/);
  await assert.rejects(()=>first.restart(),/SESSION_STALE/);
  await first.close();
  assert.throws(()=>restarted.read(),/SESSION_STALE/);
  const second=await driver.openFresh();
  assert.equal(second.page.read(),3);
  await second.close();await driver.close();
  assert.deepEqual(calls.filter(c=>c[0]==='launch'),[['launch',1,1],['launch',1,2],['launch',2,3]]);
  assert.deepEqual(calls.filter(c=>c[0]==='close'),[['close',1],['close',2],['close',3]]);
  assert.deepEqual(calls.filter(c=>c[0]==='remove'),[['remove',1],['remove',2]]);
  await assert.rejects(()=>driver.openFresh(),/DRIVER_BLOCKED/);
});
test('failed candidate browser shutdown prevents restart or fresh case and retains profile for host cleanup',async()=>{
  const {driver,calls}=candidateLifecycleFixture({closeFails:true});
  const session=await driver.openFresh();
  await assert.rejects(()=>session.restart(),/SHUTDOWN_FAILED/);
  await assert.rejects(()=>driver.openFresh(),/DRIVER_BLOCKED/);
  await assert.rejects(()=>session.close(),/CLEANUP_UNCONFIRMED/);
  await assert.rejects(()=>driver.close(),/DRIVER_BLOCKED/);
  assert.equal(calls.filter(c=>c[0]==='launch').length,1);
  assert.equal(calls.filter(c=>c[0]==='remove').length,0);
});
