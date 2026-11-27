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
    infrastructure:allCases(['browser_sandbox_confirmed','independent_driver_confirmed','browser_closed','asset_server_closed'],true),
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

import {observeCandidate} from '../tools/m4/static_web_candidate_lifecycle.mjs';
import {openCandidateAssetServer} from '../tools/m4/static_web_candidate_server.mjs';
function integratedCandidateFixture(){
  const input=admissionFixture(),admission=prepareEvaluationAdmission(input);
  const staged=prepareCandidatePayload(input,admission.admissionSha256);
  const {expected}=candidateObservationFixture();
  Object.assign(expected,{candidateSha256:staged.binding.candidateSha256,
    archiveSha256:staged.binding.archiveSha256,evaluatorSha256:admission.evaluatorSha256});
  const calls=[];
  const page={locator(){return {async innerText(){return '0';}};},
    getByRole(){return {async click(){},locator(){return {async count(){return 0;}};}};},
    keyboard:{async type(){},async press(){}},async reload(){}};
  const options={
    async securityProbe(){calls.push('security');return allCases(SECURITY_CASES,true);},
    async buildProbe(assets,files){
      calls.push('build');assert.equal(assets.assetCount,files.length);
      return {javascript_syntax:true,local_assets_linked:true};
    },
    async sandboxProbe(){calls.push('sandbox');return true;},
    async openServer(){calls.push('server-open');return {origin:'http://127.0.0.1:12345',
      async close(){calls.push('server-close');}};},
    async createDriver(){
      calls.push('driver-open');
      return {async openFresh(){calls.push('case-open');return {page,async close(){calls.push('case-close');}};},
        async close(){calls.push('driver-close');}};
    }
  };
  return {request:{payloadJson:staged.payloadJson,binding:staged.binding,expected},options,calls};
}
test('candidate lifecycle invokes the actual fixed behavioral contract and closes both resources',async()=>{
  const {request,options,calls}=integratedCandidateFixture();
  const result=await observeCandidate(request,options);
  assert.equal(result.observations.status,'TESTS_FAILED');
  assert.equal(result.observations.passed,0);
  assert.equal(result.observations.completed,7);
  assert.equal(result.observations.generatedArtifactVerified,false);
  assert.deepEqual(calls.slice(0,5),['security','build','sandbox','server-open','driver-open']);
  assert.equal(calls.filter(x=>x==='case-open').length,7);
  assert.equal(calls.filter(x=>x==='case-close').length,7);
  assert.deepEqual(calls.slice(-2),['driver-close','server-close']);
  assert.deepEqual(parseCandidateObservations(result.raw,request.expected),result.observations);
});
test('candidate lifecycle never opens browser or assets after unsafe or failed build gates',async()=>{
  for(const mode of ['security','syntax','sandbox']){
    const {request,options,calls}=integratedCandidateFixture();
    if(mode==='security')options.securityProbe=async()=>allCases(SECURITY_CASES,false);
    if(mode==='syntax')options.buildProbe=async()=>({javascript_syntax:false,local_assets_linked:true});
    if(mode==='sandbox')options.sandboxProbe=async()=>false;
    const result=await observeCandidate(request,options);
    assert.equal(result.observations.status,mode==='syntax'?'BUILD_FAILED':'VERIFICATION_BLOCKED');
    assert.equal(result.observations.completed,0);
    assert.ok(!calls.includes('server-open')&&!calls.includes('driver-open'));
  }
});
test('candidate lifecycle aborts incomplete driver observations and attempts both cleanups',async()=>{
  const {request,options,calls}=integratedCandidateFixture();
  options.createDriver=async()=>({async openFresh(){throw Error('BROWSER_PIPE_CLOSED');},
    async close(){calls.push('driver-close');throw Error('PRIVATE_DIAGNOSTIC');}});
  const result=await observeCandidate(request,options);
  assert.equal(result.observations.status,'VERIFICATION_BLOCKED');
  assert.equal(result.observations.completed,0);
  assert.equal(result.observations.infrastructure.browser_closed,false);
  assert.equal(result.observations.infrastructure.asset_server_closed,true);
  assert.deepEqual(calls.slice(-2),['driver-close','server-close']);
  assert.ok(!result.raw.includes('PRIVATE_DIAGNOSTIC'));
});
test('source integrity and asset server cleanup failures cannot become repairable test failures',async()=>{
  const {expected,report}=candidateObservationFixture();
  assert.equal(parseCandidateObservations(JSON.stringify({...report,
    build:{...report.build,source_integrity_unchanged:false}}),expected).status,'VERIFICATION_BLOCKED');
  const {request,options}=integratedCandidateFixture();
  options.openServer=async()=>({origin:'http://127.0.0.1:12345',async close(){throw Error('CLEANUP');}});
  const result=await observeCandidate(request,options);
  assert.equal(result.observations.completed,7);
  assert.equal(result.observations.infrastructure.asset_server_closed,false);
  assert.equal(result.observations.status,'VERIFICATION_BLOCKED');
});
test('real candidate asset server serves trusted fixture bytes only over loopback',async()=>{
  const input=admissionFixture(),admission=prepareEvaluationAdmission(input);
  const staged=prepareCandidatePayload(input,admission.admissionSha256);
  const assets=createCandidateAssets(staged.payloadJson,staged.binding);
  const server=await openCandidateAssetServer(assets);
  try{
    assert.match(server.origin,/^http:\/\/127\.0\.0\.1:\d+$/);
    const response=await fetch(server.origin+'/index.html');
    assert.equal(response.status,200);
    assert.equal(await response.text(),working[0][1]);
    assert.equal(response.headers.get('x-content-type-options'),'nosniff');
    for(const target of ['/','/index.html?extra=1','/missing.js','/report.json']){
      const denied=await fetch(server.origin+target);assert.equal(denied.status,404);
      await denied.text();
    }
    const denied=await fetch(server.origin+'/index.html',{method:'POST'});
    assert.equal(denied.status,404);await denied.text();
  }finally{await server.close();await server.close();}
});

import {fixedCandidateBundle,fixedCandidateFixture,parseFixedCandidateStream,qualifyFixedCandidate}
  from '../tools/m4/windows_candidate_fixture_qualification.mjs';
test('fixed candidate qualification bundles only reviewed modules and frozen fixture inputs',()=>{
  const bundle=fixedCandidateBundle();
  assert.equal(Object.keys(bundle.hashes).length,10);
  for(const name of ['working','broken','spoof']){
    const f=fixedCandidateFixture(name,bundle,'a'.repeat(64),'b'.repeat(32));
    assert.match(f.source,/data:text\/javascript;base64,/);
    assert.ok(!f.source.includes('__CA_FIXED_CANDIDATE_INPUT__'));
    assert.equal(f.expected.adapterSha256,bundle.adapterSha256);
    const encoded=f.source.match(/const INPUT='([^']+)'/)[1];
    const request=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
    assert.equal(createCandidateAssets(request.payloadJson,request.binding).entryPath,'/index.html');
    assert.deepEqual(request.expected,f.expected);
  }
  assert.throws(()=>fixedCandidateFixture('project.zip',bundle,'a'.repeat(64),'b'.repeat(32)),/SELECTION_INVALID/);
});
function fixedAdapterDocker({failCleanup=false,wrongNegative=false}={}){
  const calls=[];let id=0,active=null;
  const docker=(args,timeout,source)=>{
    calls.push(args);
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify(info)};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify(meta)};
    if(args[0]==='ps')return {status:0,stdout:active?'leftover':''};
    if(args[0]==='run'){
      const request=JSON.parse(Buffer.from(source.match(/const INPUT='([^']+)'/)[1],'base64').toString('utf8'));
      const {report}=candidateObservationFixture();
      Object.assign(report,request.expected);
      report.cases=allCases(CALCULATION_CASES,id++===0||wrongNegative);
      active=args[args.indexOf('--name')+1];
      return {status:0,stdout:JSON.stringify({kind:'progress',stage:'COMPLETE'})+'\n'+JSON.stringify(report)};
    }
    if(args[0]==='container')return {status:0,stdout:JSON.stringify({'codingassistant.aet8':active})};
    if(args[0]==='rm'){if(!failCleanup)active=null;return {status:0,stdout:''};}
    throw Error('unexpected Docker command');
  };
  return {docker,calls};
}
test('fixed adapter qualification requires all three controls and confirmed cleanup without granting execution',()=>{
  const f=fixedAdapterDocker();let i=0;
  const result=qualifyFixedCandidate({platform:'win32',env:{},docker:f.docker,makeId:()=>String(++i).repeat(32)});
  assert.equal(result.status,'FIXED_CANDIDATE_ADAPTER_PASS',result.reason);
  assert.deepEqual(Object.values(result.fixtures).map(x=>x.observations.passed),[7,0,0]);
  assert.ok(Object.values(result.fixtures).every(x=>x.containerRemoved));
  assert.equal(result.runtimeQualified,false);
  assert.equal(result.generatedSourceExecuted,false);
  for(const args of f.calls.filter(x=>x[0]==='run')){
    for(const required of ['--network=none','--read-only','--cap-drop=all','--pull=never','--user=1000:1000'])
      assert.ok(args.includes(required));
    assert.ok(!args.includes('--mount')&&!args.includes('--privileged')&&!args.includes('--no-sandbox'));
    assert.equal(args.at(-1),'--run-fixed-candidate-fixture');
  }
});
test('fixed adapter qualification stops on cleanup failure, unexpected negative or unsupported host',()=>{
  for(const options of [{failCleanup:true},{wrongNegative:true}]){
    const f=fixedAdapterDocker(options);let i=0;
    const result=qualifyFixedCandidate({platform:'win32',env:{},docker:f.docker,makeId:()=>String(++i).repeat(32)});
    assert.equal(result.status,'BLOCKED',JSON.stringify(options));
    assert.equal(result.reason,options.failCleanup?'CONTAINER_CLEANUP_UNCONFIRMED':'FIXTURE_EXPECTATIONS_NOT_MET');
    assert.equal(f.calls.filter(x=>x[0]==='run').length,options.failCleanup?1:2);
  }
  const f=fixedAdapterDocker();
  assert.equal(qualifyFixedCandidate({platform:'linux',env:{},docker:f.docker}).status,'BLOCKED');
  assert.equal(f.calls.length,0);
});
test('fixed adapter CLI is inert without explicit run and rejects arbitrary source arguments',()=>{
  for(const args of [[],['--run','project.zip'],['--zip','project.zip']]){
    const result=spawnSync(process.execPath,['tools/m4/windows_candidate_fixture_qualification.mjs',...args],{encoding:'utf8',timeout:5000});
    assert.equal(result.status,args.length===0?0:2);
    if(args.length===0)assert.match(result.stdout,/NOT_RUN/);
  }
});


import {preparePilotPolicy} from '../tools/m4/static_web_pilot_policy.mjs';
import {checkedChromiumSeccomp} from '../tools/m4/windows_chromium_sandbox_qualification.mjs';
function pilotPolicyFixture(){
  const image={...meta,Config:{Env:['PATH=/usr/local/bin:/usr/bin:/bin'],Volumes:null,ExposedPorts:null}};
  const policy=preparePilotPolicy(image,'a'.repeat(64),'b'.repeat(64));
  const attempt='c'.repeat(32),containerId='d'.repeat(64),item=policy.identity(attempt);
  const seccomp=JSON.parse(fs.readFileSync(checkedChromiumSeccomp().path,'utf8'));
  const inspect={Id:containerId,Name:'/'+item.name,Image:image.Id,State:{Status:'created',Running:false},
    Config:{Image:image.Id,Labels:{[item.label]:attempt,'codingassistant.pilot-policy':policy.policySha256},
      User:'1000:1000',OpenStdin:true,Tty:false,Entrypoint:['node'],Cmd:['--input-type=module','-','--run-fixed-candidate-fixture'],
      Env:[...image.Config.Env,'HOME=/tmp','XDG_CONFIG_HOME=/tmp/config','XDG_CACHE_HOME=/tmp/cache'],
      Volumes:null,ExposedPorts:null},
    HostConfig:{NetworkMode:'none',IpcMode:'private',PidMode:'',UTSMode:'',Privileged:false,ReadonlyRootfs:true,
      Init:true,AutoRemove:false,Memory:2147483648,MemorySwap:2147483648,NanoCpus:2000000000,PidsLimit:256,
      ShmSize:268435456,CapDrop:['ALL'],CapAdd:null,Binds:null,Mounts:null,VolumesFrom:null,Devices:[],
      DeviceRequests:null,DeviceCgroupRules:null,PortBindings:{},PublishAllPorts:false,ExtraHosts:null,Links:null,
      Dns:[],DnsOptions:[],DnsSearch:[],RestartPolicy:{Name:'no'},Tmpfs:{'/tmp':'rw,nosuid,nodev,mode=1777,size=256m'},
      SecurityOpt:['no-new-privileges','seccomp='+JSON.stringify(seccomp)]},Mounts:[]};
  return {image,policy,attempt,containerId,inspect};
}
test('separate pilot policy builds fixed create-only arguments and audits exact stopped container configuration',()=>{
  const {policy,attempt,containerId,inspect}=pilotPolicyFixture();
  const args=policy.createArgs(attempt);
  assert.equal(args[0],'create');
  for(const flag of ['--pull=never','--network=none','--read-only','--cap-drop=all','--memory-swap=2g','--pids-limit=256','--user=1000:1000'])
    assert.ok(args.includes(flag));
  for(const flag of ['--rm','--mount','--privileged','--no-sandbox','--pid=host'])assert.ok(!args.includes(flag));
  assert.equal(policy.audit(attempt,containerId,inspect).status,'CONTAINER_CONFIGURATION_AUDITED');
  assert.equal(policy.audit(attempt,containerId,inspect).executionEnabled,false);
  assert.throws(()=>policy.createArgs('../anything'),/ATTEMPT_INVALID/);
  assert.throws(()=>policy.audit(attempt,'e'.repeat(64),inspect),/IDENTITY_MISMATCH/);
});
test('pilot container inspection blocks actual policy drift before any candidate can start',()=>{
  const {policy,attempt,containerId,inspect}=pilotPolicyFixture();
  for(const [field,value] of Object.entries({NetworkMode:'bridge',Privileged:true,ReadonlyRootfs:false,
    Init:false,AutoRemove:true,PidMode:'host',IpcMode:'host',Memory:0,MemorySwap:-1,NanoCpus:0,PidsLimit:257,
    CapAdd:['SYS_ADMIN'],Binds:['C:/Users:/host'],Devices:[{}],DeviceRequests:[{}],PublishAllPorts:true,
    PortBindings:{'80/tcp':[{HostPort:'80'}]},RestartPolicy:{Name:'always'},Tmpfs:{'/tmp':'rw'},
    SecurityOpt:['seccomp=unconfined','no-new-privileges']})){
    const changed=structuredClone(inspect);changed.HostConfig[field]=value;
    assert.throws(()=>policy.audit(attempt,containerId,changed),/PILOT_CONTAINER_/,field);
  }
  for(const modify of [
    i=>{i.Config.Env.push('OPENAI_API_KEY=secret');},
    i=>{i.Config.Cmd=['-e','arbitrary'];},
    i=>{i.Config.User='0';},
    i=>{i.State.Running=true;},
    i=>{i.Mounts.push({Type:'bind',Destination:'/host'});},
    i=>{i.Config.Labels['codingassistant.pilot-policy']='0'.repeat(64);}]){
    const changed=structuredClone(inspect);modify(changed);
    assert.throws(()=>policy.audit(attempt,containerId,changed));
  }
});
test('pilot policy pins image environment and distinct adapter/evaluator identities without executable authorization',()=>{
  const {image,policy}=pilotPolicyFixture();
  assert.notEqual(preparePilotPolicy(image,'c'.repeat(64),'b'.repeat(64)).policySha256,policy.policySha256);
  assert.notEqual(preparePilotPolicy(image,'a'.repeat(64),'c'.repeat(64)).policySha256,policy.policySha256);
  assert.throws(()=>preparePilotPolicy({...image,Config:{...image.Config,Volumes:{'/host':{}}}},'a'.repeat(64),'b'.repeat(64)),/IMAGE_CONFIG_UNSAFE/);
  assert.throws(()=>preparePilotPolicy({...image,Config:{...image.Config,Env:['GH_TOKEN=secret']}},'a'.repeat(64),'b'.repeat(64)),/ENVIRONMENT_INVALID/);
});


import {qualifyPilotConfiguration} from '../tools/m4/windows_pilot_configuration_qualification.mjs';
function pilotConfigurationDocker({drift=false,leftover=false,prior=false,wrongOwner=false,createFailed=false}={}){
  const {image,attempt,containerId,inspect}=pilotPolicyFixture();
  const calls=[];let active=false;
  const docker=args=>{
    calls.push(args);
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify(info)};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify(image)};
    if(args[0]==='ps')return {status:0,stdout:active||prior?containerId:''};
    if(args[0]==='create'){
      active=true;
      const labels=args.flatMap((a,i)=>a==='--label'?[args[i+1]]:[]);
      inspect.Config.Labels=Object.fromEntries(labels.map(x=>x.split('=')));
      if(drift)inspect.HostConfig.NetworkMode='bridge';
      if(wrongOwner)inspect.Config.Labels['codingassistant.pilot-policy']='0'.repeat(64);
      return createFailed?{status:125,stdout:''}:{status:0,stdout:containerId};
    }
    if(args[0]==='container')return {status:0,stdout:JSON.stringify(inspect)};
    if(args[0]==='rm'){if(!leftover)active=false;return {status:0,stdout:containerId};}
    throw Error('unexpected Docker command');
  };
  return {docker,calls,attempt};
}
test('pilot configuration probe creates stopped container, audits actual configuration and removes by exact ID',()=>{
  const f=pilotConfigurationDocker();
  const result=qualifyPilotConfiguration({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt});
  assert.equal(result.status,'PILOT_CONTAINER_CONFIGURATION_PASS',result.reason);
  assert.equal(result.configurationAudited,true);
  assert.equal(result.containerRemoved,true);
  assert.equal(result.guestStarted,false);
  assert.equal(result.runtimeQualified,false);
  assert.equal(result.generatedCodeExecutionEnabled,false);
  assert.ok(f.calls.some(args=>args[0]==='create'));
  assert.ok(f.calls.some(args=>args[0]==='rm'&&args[1]==='d'.repeat(64)));
  assert.ok(!f.calls.some(args=>['run','start','exec'].includes(args[0])));
});
test('pilot configuration probe refuses drift, previous cleanup debt and wrong ownership without starting a guest',()=>{
  for(const [scenario,reason,remove] of [
    [{drift:true},'PILOT_CONTAINER_ISOLATION_MISMATCH',true],
    [{leftover:true},'PILOT_CONTAINER_CLEANUP_UNCONFIRMED',true],
    [{prior:true},'PILOT_PRIOR_CLEANUP_REQUIRED',false],
    [{wrongOwner:true},'PILOT_CONTAINER_CLEANUP_UNCONFIRMED',false],
    [{createFailed:true},'PILOT_DOCKER_QUERY_FAILED',true]]){
    const f=pilotConfigurationDocker(scenario);
    const result=qualifyPilotConfiguration({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt});
    assert.equal(result.status,'BLOCKED',JSON.stringify(scenario));
    assert.equal(result.reason,reason);
    assert.equal(f.calls.some(args=>args[0]==='rm'),remove);
    assert.ok(!f.calls.some(args=>['run','start','exec'].includes(args[0])));
  }
});
test('pilot configuration CLI is inert without explicit run and rejects source paths',()=>{
  for(const args of [[],['--run','project.zip']]){
    const result=spawnSync(process.execPath,['tools/m4/windows_pilot_configuration_qualification.mjs',...args],
      {encoding:'utf8',timeout:5000});
    assert.equal(result.status,args.length===0?0:2,result.stderr);
    if(args.length===0)assert.match(result.stdout,/NOT_RUN/);
  }
});


import {reservePilotAttempt,readPilotAttempt,recoverPilotAttempt} from '../tools/m4/static_web_pilot_attempt_journal.mjs';
import {qualifyPilotLifecycle,reconcilePilotLifecycle} from '../tools/m4/windows_pilot_lifecycle_qualification.mjs';
function withPilotJournal(callback){
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-pilot-journal-'));
  try{return callback(root);}finally{fs.rmSync(root,{recursive:true,force:true});}
}
function journalIdentity(){
  const {policy,attempt}=pilotPolicyFixture();
  return {...policy.identity(attempt),engineId:'engine-test',
    candidateSha256:'1'.repeat(64),archiveSha256:'2'.repeat(64)};
}
test('pilot attempt journal persists exact state, rejects replay, illegal transitions and live recovery',()=>withPilotJournal(root=>{
  const identity=journalIdentity(),j=reservePilotAttempt(root,identity);
  assert.throws(()=>reservePilotAttempt(root,identity),/PRIOR_CLEANUP_REQUIRED/);
  assert.throws(()=>j.advance('Starting','d'.repeat(64)),/TRANSITION_INVALID/);
  j.advance('Creating');j.advance('Created','d'.repeat(64));j.advance('Audited');
  assert.deepEqual(readPilotAttempt(root).current,{sequence:3,stage:'Audited',containerId:'d'.repeat(64)});
  assert.throws(()=>recoverPilotAttempt(root,{isAlive:()=>true}),/OWNER_STILL_ACTIVE/);
  j.advance('CleanupPending');assert.throws(()=>j.archive(),/CLEANUP_REQUIRED/);
  j.advance('Cleaned');j.archive();
  assert.equal(readPilotAttempt(root),null);
  assert.throws(()=>reservePilotAttempt(root,identity),/ATTEMPT_REPLAY/);
}));
test('pilot attempt recovery claims interrupted journal once and never grants a new start',()=>withPilotJournal(root=>{
  const identity=journalIdentity(),j=reservePilotAttempt(root,identity);
  j.advance('Creating');j.advance('Created','d'.repeat(64));j.advance('Audited');j.advance('Starting');
  const recovered=recoverPilotAttempt(root,{isAlive:()=>false});
  assert.equal(recovered.current.stage,'Starting');
  assert.throws(()=>recovered.advance('Exited'),/RECOVERY_CLEANUP_ONLY/);
  assert.throws(()=>j.advance('Exited'),/RECOVERY_ALREADY_CLAIMED/);
  assert.throws(()=>recoverPilotAttempt(root,{isAlive:()=>false}),/RECOVERY_ALREADY_CLAIMED/);
  recovered.advance('CleanupPending');recovered.advance('Cleaned');recovered.archive();
  assert.equal(readPilotAttempt(root),null);
}));
function durablePilotDocker({badExit=false,cleanupFails=false,wrongOwner=false,negative=false}={}){
  const {image,attempt,containerId,inspect}=pilotPolicyFixture();
  const calls=[];let active=false;
  const docker=(args,timeout,source)=>{
    calls.push(args);
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify({...info,ID:'engine-test'})};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify(image)};
    if(args[0]==='ps')return {status:0,stdout:active?containerId:''};
    if(args[0]==='create'){
      active=true;inspect.Config.Labels=Object.fromEntries(args.flatMap((a,i)=>a==='--label'?[args[i+1].split('=')]:[]));
      if(wrongOwner)inspect.Config.Labels['codingassistant.pilot-policy']='0'.repeat(64);
      return {status:0,stdout:containerId};
    }
    if(args[0]==='container')return {status:0,stdout:JSON.stringify(inspect)};
    if(args[0]==='start'){
      inspect.State={Status:'exited',Running:false,ExitCode:badExit?2:0};
      const request=JSON.parse(Buffer.from(source.match(/const INPUT='([^']+)'/)[1],'base64').toString('utf8'));
      const {report}=candidateObservationFixture();Object.assign(report,request.expected);
      if(negative)report.cases=allCases(CALCULATION_CASES,false);
      return {status:badExit?2:0,stdout:JSON.stringify({kind:'progress',stage:'COMPLETE'})+'\n'+JSON.stringify(report)};
    }
    if(args[0]==='rm'){if(!cleanupFails)active=false;return {status:0,stdout:containerId};}
    throw Error('unexpected Docker command');
  };
  return {docker,calls,attempt,inspect,containerId,setActive:()=>{active=true;}};
}
test('durable pilot qualification audits before fixed start and archives only after confirmed cleanup',()=>withPilotJournal(root=>{
  const f=durablePilotDocker();
  const result=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt,root});
  assert.equal(result.status,'PILOT_DURABLE_FIXED_CONTROL_PASS',result.reason);
  assert.equal(result.observations.passed,7);assert.equal(result.containerRemoved,true);
  assert.equal(result.generatedCodeExecutionEnabled,false);assert.equal(readPilotAttempt(root),null);
  const commands=f.calls.map(x=>x[0]);assert.ok(commands.indexOf('create')<commands.indexOf('container'));
  assert.ok(commands.indexOf('container')<commands.indexOf('start'));assert.ok(commands.indexOf('start')<commands.indexOf('rm'));
  assert.ok(!commands.includes('run')&&!commands.includes('exec'));
}));
test('durable pilot blocks failed exit, wrong ownership and cleanup debt without replay',()=>{
  for(const scenario of [{badExit:true},{wrongOwner:true},{cleanupFails:true}]){
    withPilotJournal(root=>{
      const f=durablePilotDocker(scenario);
      const result=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt,root});
      assert.equal(result.status,'BLOCKED',JSON.stringify(scenario));
      assert.equal(result.cleanupDebt,!scenario.badExit);
      if(scenario.wrongOwner)assert.ok(!f.calls.some(x=>x[0]==='start'||x[0]==='rm'));
      if(result.cleanupDebt){
        const count=f.calls.length;
        const again=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,attemptId:'e'.repeat(32),root});
        assert.equal(again.reason,'PILOT_PRIOR_CLEANUP_REQUIRED');assert.equal(f.calls.length,count);
      }
    });
  }
});
test('explicit interrupted pilot recovery removes only exact owned container and never starts or replays',()=>withPilotJournal(root=>{
  const f=durablePilotDocker(),identity=journalIdentity();
  const j=reservePilotAttempt(root,identity);j.advance('Creating');j.advance('Created',f.containerId);j.advance('Audited');j.advance('Starting');
  f.setActive();
  f.inspect.Config.Labels[identity.label]=identity.attemptId;
  f.inspect.Config.Labels['codingassistant.pilot-policy']=identity.policySha256;
  f.inspect.State={Status:'running',Running:true};
  const result=reconcilePilotLifecycle({platform:'win32',env:{},docker:f.docker,root,isAlive:()=>false});
  assert.equal(result.status,'PILOT_INTERRUPTED_ATTEMPT_RECONCILED',result.reason);
  assert.equal(result.containerRemoved,true);assert.equal(readPilotAttempt(root),null);
  assert.ok(f.calls.some(x=>x[0]==='rm'&&x[1]==='-f'&&x[2]===f.containerId));
  assert.ok(!f.calls.some(x=>['create','start','run','exec'].includes(x[0])));
}));
test('ambiguous create interruption with no exact container stays blocked rather than clearing debt',()=>withPilotJournal(root=>{
  const f=durablePilotDocker(),j=reservePilotAttempt(root,journalIdentity());j.advance('Creating');
  const result=reconcilePilotLifecycle({platform:'win32',env:{},docker:f.docker,root,isAlive:()=>false});
  assert.equal(result.reason,'PILOT_CREATE_OUTCOME_UNCERTAIN');
  assert.equal(result.cleanupDebt,true);assert.equal(readPilotAttempt(root).current.stage,'Creating');
  assert.ok(!f.calls.some(x=>x[0]==='rm'||x[0]==='start'));
}));

test('truncated active journal blocks all new work and recovery without deleting evidence',()=>withPilotJournal(root=>{
  const j=reservePilotAttempt(root,journalIdentity());j.advance('Creating');
  const file=path.join(root,'active','events.jsonl');
  fs.appendFileSync(file,'{"sequence":2');
  assert.throws(()=>readPilotAttempt(root),/JOURNAL_INVALID/);
  assert.throws(()=>recoverPilotAttempt(root,{isAlive:()=>false}),/JOURNAL_INVALID/);
  assert.throws(()=>reservePilotAttempt(root,{...journalIdentity(),attemptId:'e'.repeat(32),name:'ca-win-pilot-'+'e'.repeat(32)}),/PRIOR_CLEANUP_REQUIRED/);
  assert.ok(fs.readFileSync(file,'utf8').endsWith('{"sequence":2'));
}));


import {observeRunningPilot,cancelObservedPilot} from '../tools/m4/windows_pilot_interruption_qualification.mjs';
test('durable negative controls require all seven observed failures and reject unrelated input',()=>{
  for(const control of ['broken','spoof'])withPilotJournal(root=>{
    const f=durablePilotDocker({negative:true});
    const result=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt,root,control});
    assert.equal(result.status,'PILOT_DURABLE_FIXED_CONTROL_PASS',result.reason);
    assert.equal(result.observations.completed,7);assert.equal(result.observations.passed,0);
    assert.equal(result.containerRemoved,true);
  });
  withPilotJournal(root=>{
    const f=durablePilotDocker();
    const result=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,attemptId:f.attempt,root,control:'broken'});
    assert.equal(result.reason,'PILOT_FIXED_CONTROL_FAILED');assert.equal(result.containerRemoved,true);
    const blocked=qualifyPilotLifecycle({platform:'win32',env:{},docker:f.docker,root,control:'project.zip'});
    assert.equal(blocked.reason,'PILOT_FIXED_CONTROL_REQUIRED');
  });
});
test('interruption requires exact owner, running state, engine, and container identity before signaling',()=>withPilotJournal(root=>{
  const identity=journalIdentity(),j=reservePilotAttempt(root,identity);
  const {inspect,containerId}=pilotPolicyFixture();
  const calls=[];let engine='engine-test';
  const host={engine:()=>engine,inspect:()=>inspect,query:args=>{calls.push(args);return containerId;}};
  assert.equal(observeRunningPilot(root,process.pid,host),null);
  j.advance('Creating');j.advance('Created',containerId);j.advance('Audited');j.advance('Starting');
  assert.throws(()=>observeRunningPilot(root,process.pid+1,host),/OWNER_MISMATCH/);
  assert.equal(observeRunningPilot(root,process.pid,host),null);
  inspect.State={Status:'running',Running:true};
  const item=observeRunningPilot(root,process.pid,host);
  engine='other';assert.throws(()=>cancelObservedPilot(item,host),/NOT_CURRENT/);
  engine='engine-test';inspect.Config.Labels[identity.label]='0'.repeat(32);
  assert.throws(()=>cancelObservedPilot(item,host),/OWNERSHIP_MISMATCH/);
  inspect.Config.Labels[identity.label]=identity.attemptId;
  cancelObservedPilot(item,host);
  assert.deepEqual(calls,[['kill','--signal=KILL',containerId]]);
  inspect.State={Status:'exited',Running:false};
  assert.throws(()=>cancelObservedPilot(item,host),/NOT_CURRENT/);
  assert.equal(calls.length,1);
}));
test('real interruption qualification CLI is inert without explicit run and rejects project input',()=>{
  for(const args of [[],['--run','project.zip']]){
    const result=spawnSync(process.execPath,['tools/m4/windows_pilot_interruption_qualification.mjs',...args],
      {encoding:'utf8',timeout:5000});
    assert.equal(result.status,args.length===0?0:2,result.stderr);
    if(args.length===0)assert.match(result.stdout,/NOT_RUN/);
  }
});


import {readArchivedPilotAttempt} from '../tools/m4/static_web_pilot_attempt_journal.mjs';
import {bindPilotEvidence} from '../tools/m4/static_web_pilot_evidence.mjs';
function pilotEvidenceFixture(root){
  const {image}=pilotPolicyFixture(),engineId='engine-test',bundle=fixedCandidateBundle();
  const policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
  const base={schemaVersion:1,recordedAtUtc:'2026-11-27T12:00:00Z',
    generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false};
  let n=0;
  function archived(name,mode=null){
    const attemptId=String(++n).repeat(32),fixture=fixedCandidateFixture(name,bundle,policy.policySha256,attemptId);
    const identity={...policy.identity(attemptId),engineId,candidateSha256:fixture.expected.candidateSha256,
      archiveSha256:fixture.expected.archiveSha256};
    let j=reservePilotAttempt(root,identity);
    j.advance('Creating');j.advance('Created',String(n).repeat(64));j.advance('Audited');j.advance('Starting');
    if(!mode)j.advance('Exited');
    if(mode==='crash')j=recoverPilotAttempt(root,{isAlive:()=>false});
    j.advance('CleanupPending');j.advance('Cleaned');j.archive();
    if(mode)return {mode,attemptId,observedRunning:true,interruptionRequested:true,childExitConfirmed:true,
      containerRemoved:true,cleanupDebt:false,reason:null};
    const {report}=candidateObservationFixture();
    Object.assign(report,fixture.expected);report.cases=allCases(CALCULATION_CASES,name==='working');
    return {...base,stage:'AET-8-pilot-durable-lifecycle',status:'PILOT_DURABLE_FIXED_CONTROL_PASS',
      attemptId,policySha256:policy.policySha256,engineId,configurationAudited:true,
      observations:parseCandidateObservations(JSON.stringify(report),fixture.expected),containerRemoved:true,cleanupDebt:false,reason:null};
  }
  return {working:archived('working'),negatives:{...base,stage:'AET-8-pilot-negative-controls',
    status:'PILOT_DURABLE_NEGATIVE_CONTROLS_PASS',controls:{broken:archived('broken'),spoof:archived('spoof')}},
    interruptions:{...base,stage:'AET-8-pilot-real-interruption-controls',status:'PILOT_REAL_INTERRUPTION_CONTROLS_PASS',
      controls:{cancel:archived('working','cancel'),crash:archived('working','crash')},reason:null},
    imageMetadata:image,engineId,root};
}
test('pilot historical evidence binds five distinct archived attempts without granting runtime or host trust',()=>withPilotJournal(root=>{
  const input=pilotEvidenceFixture(root);
  const bound=bindPilotEvidence(input);
  assert.equal(bound.status,'PILOT_HISTORICAL_EVIDENCE_BOUND');
  assert.equal(Object.keys(bound.attempts).length,5);
  assert.equal(bound.hostProvenanceEstablished,false);assert.equal(bound.executionEnabled,false);
  assert.equal(bound.runtimeQualified,false);
  assert.equal(bindPilotEvidence(input).evidenceSha256,bound.evidenceSha256);
  assert.equal(readArchivedPilotAttempt(root,input.interruptions.controls.crash.attemptId).recoveryClaimed,true);
}));
test('pilot historical evidence rejects forged outcomes, mismatched engine, duplicate attempts and omitted controls',()=>withPilotJournal(root=>{
  const input=pilotEvidenceFixture(root);
  for(const change of [
    x=>{x.working.observations.passed=0;},
    x=>{x.working.observations.security[SECURITY_CASES[0]]=false;},
    x=>{x.engineId='other-engine';},
    x=>{delete x.negatives.controls.spoof;},
    x=>{x.interruptions.controls.crash.attemptId=x.interruptions.controls.cancel.attemptId;},
    x=>{x.interruptions.controls.cancel.containerRemoved=false;},
    x=>{x.working.executionEnabled=true;}]){
    const changed=structuredClone(input);change(changed);assert.throws(()=>bindPilotEvidence(changed));
  }
}));
test('pilot historical evidence requires complete unchanged archived journals and blocks active cleanup debt',()=>withPilotJournal(root=>{
  const input=pilotEvidenceFixture(root);
  const j=reservePilotAttempt(root,{...journalIdentity(),attemptId:'e'.repeat(32),name:'ca-win-pilot-'+'e'.repeat(32)});
  assert.throws(()=>bindPilotEvidence(input),/PRIOR_CLEANUP_REQUIRED/);
  j.advance('CleanupPending');j.advance('Cleaned');j.archive();
  const file=path.join(root,'attempt-'+input.working.attemptId,'events.jsonl');
  const original=fs.readFileSync(file,'utf8');
  fs.writeFileSync(file,original.replace('"stage":"Audited"','"stage":"Starting"'));
  assert.throws(()=>bindPilotEvidence(input),/JOURNAL_INVALID/);
}));
test('pilot evidence inspection CLI is inert and rejects missing receipt selections',()=>{
  for(const args of [[],['--inspect','one.json']]){
    const result=spawnSync(process.execPath,['tools/m4/static_web_pilot_evidence.mjs',...args],{encoding:'utf8',timeout:5000});
    assert.equal(result.status,args.length===0?0:2,result.stderr);
  }
});


import {fixedBrowserBoundaryBundle,fixedBrowserBoundaryFixture,parseBrowserBoundaryReport}
  from '../tools/m4/static_web_pilot_browser_boundary.mjs';
test('browser boundary qualification reuses exact candidate module closure with distinct fixed probe identity',()=>{
  const original=fixedCandidateBundle(),bundle=fixedBrowserBoundaryBundle();
  assert.notEqual(bundle.adapterSha256,original.adapterSha256);
  assert.equal(bundle.evaluatorSha256,original.evaluatorSha256);
  const fixture=fixedBrowserBoundaryFixture(bundle,'a'.repeat(64),'b'.repeat(32));
  assert.ok(!fixture.source.includes('__CA_FIXED_CANDIDATE_INPUT__'));
  const encoded=fixture.source.match(/const INPUT='([^']+)'/)[1];
  const request=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
  const assets=createCandidateAssets(request.payloadJson,request.binding);
  assert.equal(assets.assetCount,2);
  assert.match(assets.resolve('GET','/probe.js').body,/BOUNDARY_CONTROLS_PASS/);
  assert.equal(assets.resolve('GET','/report.json').status,404);
});
test('browser boundary receipt requires exact attempt and all five independent control categories',()=>{
  const bundle=fixedBrowserBoundaryBundle(),fixture=fixedBrowserBoundaryFixture(bundle,'a'.repeat(64),'b'.repeat(32));
  const report={schemaVersion:1,...fixture.expected,checks:allCases(
    ['guest_security','chromium_sandbox','renderer_controls','browser_closed','server_closed'],true)};
  const parse=x=>parseBrowserBoundaryReport(JSON.stringify(x),fixture.expected);
  assert.equal(parse(report).status,'FIXED_BROWSER_BOUNDARY_PASS');
  assert.equal(parse(report).runtimeQualified,false);
  assert.equal(parse({...report,checks:{...report.checks,renderer_controls:false}}).status,'VERIFICATION_BLOCKED');
  assert.throws(()=>parse({...report,attemptId:'c'.repeat(32)}),/REPORT_INVALID/);
  assert.throws(()=>parse({...report,status:'VERIFIED'}),/REPORT_INVALID/);
  assert.throws(()=>parse({...report,checks:{...report.checks,browser_closed:'true'}}),/REPORT_INVALID/);
});


import {bindCompletePilotEvidence} from '../tools/m4/static_web_pilot_evidence.mjs';
function completePilotEvidenceFixture(root){
  const input=pilotEvidenceFixture(root);
  const bundle=fixedBrowserBoundaryBundle(),policy=preparePilotPolicy(input.imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  const attemptId='6'.repeat(32),fixture=fixedBrowserBoundaryFixture(bundle,policy.policySha256,attemptId);
  const j=reservePilotAttempt(root,{...policy.identity(attemptId),engineId:input.engineId,
    candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256});
  j.advance('Creating');j.advance('Created','6'.repeat(64));j.advance('Audited');j.advance('Starting');
  j.advance('Exited');j.advance('CleanupPending');j.advance('Cleaned');j.archive();
  const observations=parseBrowserBoundaryReport(JSON.stringify({schemaVersion:1,...fixture.expected,
    checks:allCases(['guest_security','chromium_sandbox','renderer_controls','browser_closed','server_closed'],true)}),fixture.expected);
  input.boundary={...input.working,status:'PILOT_BROWSER_BOUNDARY_PASS',attemptId,policySha256:policy.policySha256,observations};
  return input;
}
test('complete historical evidence binds separate browser probe without changing five-attempt digest',()=>withPilotJournal(root=>{
  const input=completePilotEvidenceFixture(root),prior=bindPilotEvidence(input),complete=bindCompletePilotEvidence(input);
  assert.equal(complete.status,'PILOT_COMPLETE_HISTORICAL_EVIDENCE_BOUND');
  assert.equal(complete.evidenceSha256,prior.evidenceSha256);
  assert.match(complete.completeEvidenceSha256,/^[a-f0-9]{64}$/);
  assert.notEqual(complete.boundary.policySha256,complete.policySha256);
  assert.equal(complete.boundary.attemptId,input.boundary.attemptId);
  assert.equal(complete.hostProvenanceEstablished,false);
  assert.equal(complete.executionEnabled,false);
}));
test('complete evidence blocks altered browser receipt or reuse of prior attempt',()=>withPilotJournal(root=>{
  const input=completePilotEvidenceFixture(root);
  for(const change of [
    x=>{x.boundary.policySha256=x.working.policySha256;},
    x=>{x.boundary.attemptId=x.working.attemptId;},
    x=>{x.boundary.observations.checks.renderer_controls=false;},
    x=>{x.boundary.containerRemoved=false;},
    x=>{delete x.boundary;}]){
    const changed=structuredClone(input);change(changed);assert.throws(()=>bindCompletePilotEvidence(changed));
  }
}));
test('pilot evidence stdin rejects missing pinned Docker identity and extra request fields',()=>{
  const receiptNames=Array.from({length:4},(_,i)=>'windows-static-web-'+String(i).repeat(12)+'.json');
  const env={...process.env};delete env.CA_EVALUATION_DOCKER_EXE;delete env.CA_EVALUATION_DOCKER_SHA256;
  for(const [input,reason] of [
    [{operation:'inspect-complete-pilot',receiptNames},'EVALUATION_DOCKER_PROVENANCE_REQUIRED'],
    [{operation:'inspect-complete-pilot',receiptNames,executionEnabled:true},'PILOT_RECEIPT_SELECTION_INVALID']]){
    const result=spawnSync(process.execPath,['tools/m4/static_web_pilot_evidence.mjs','--stdin'],
      {env,input:JSON.stringify(input),encoding:'utf8',timeout:5000});
    assert.equal(result.status,2);assert.equal(JSON.parse(result.stdout).reason,reason);
  }
});


import {prepareReviewedCandidateTransport} from '../tools/m4/static_web_candidate_transport.mjs';
function transportFixture(root){
  const pilot=completePilotEvidenceFixture(root),inputs=admissionFixture();
  // Same synthetic image as the historical fixture, no real host action.
  assert.equal(inputs.currentImageMetadata.Id,pilot.imageMetadata.Id);
  assert.deepEqual(inputs.currentImageMetadata.RepoDigests,pilot.imageMetadata.RepoDigests);
  const admission=prepareEvaluationAdmission(inputs),historical=bindCompletePilotEvidence(pilot);
  return {inputs,pilot,selection:{attemptId:'e'.repeat(32),admissionSha256:admission.admissionSha256,
    candidateSha256:admission.candidateSha256,archiveSha256:admission.archiveSha256,
    completeEvidenceSha256:historical.completeEvidenceSha256,policySha256:historical.policySha256,
    adapterSha256:historical.policy.adapterSha256}};
}
test('exact reviewed candidate transport preserves qualified template and carries only data',()=>withPilotJournal(root=>{
  const f=transportFixture(root),prepared=prepareReviewedCandidateTransport(f.inputs,f.pilot,f.selection);
  const {descriptor,source,expected}=prepared;
  assert.equal(descriptor.status,'CANDIDATE_TRANSPORT_PREPARED_ONLY');
  assert.equal(descriptor.executionEnabled,false);assert.equal(descriptor.runtimeQualified,false);
  assert.equal(descriptor.transportSha256,createHash('sha256').update(source).digest('hex'));
  assert.equal(descriptor.transportBytes,Buffer.byteLength(source));
  const encoded=source.match(/const INPUT='([^']+)'/)[1];
  assert.equal(source.replace(encoded,'__CA_FIXED_CANDIDATE_INPUT__'),fixedCandidateBundle().source);
  const request=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
  assert.deepEqual(request.expected,expected);
  const assets=createCandidateAssets(request.payloadJson,request.binding);
  assert.equal(assets.resolve('GET','/index.html').body,working[0][1]);
  assert.equal(assets.resolve('GET','/app.js').body,working[1][1]);
  assert.equal(prepareReviewedCandidateTransport(f.inputs,f.pilot,f.selection).descriptor.transportSha256,descriptor.transportSha256);
}));
test('transport preparation refuses changed source, evidence, policy, adapter and reused attempts',()=>withPilotJournal(root=>{
  const f=transportFixture(root);
  for(const key of ['admissionSha256','candidateSha256','archiveSha256','completeEvidenceSha256','policySha256','adapterSha256'])
    assert.throws(()=>prepareReviewedCandidateTransport(f.inputs,f.pilot,{...f.selection,[key]:'0'.repeat(64)}),/BINDING_CHANGED/);
  assert.throws(()=>prepareReviewedCandidateTransport(f.inputs,f.pilot,{...f.selection,attemptId:f.pilot.working.attemptId}),/ATTEMPT_REUSED/);
  assert.throws(()=>prepareReviewedCandidateTransport(f.inputs,f.pilot,{...f.selection,executionEnabled:true}),/SELECTION_INVALID/);
  assert.throws(()=>prepareReviewedCandidateTransport({...f.inputs,archive:buildZip([['index.html','changed']])},f.pilot,f.selection));
  const changed=structuredClone(f.pilot);changed.boundary.containerRemoved=false;
  assert.throws(()=>prepareReviewedCandidateTransport(f.inputs,changed,f.selection));
}));
test('transport encodes source punctuation and marker text as data rather than substituting guest code',()=>withPilotJournal(root=>{
  const f=transportFixture(root);
  const files=[['index.html',working[0][1]],['app.js',"// __CA_FIXED_CANDIDATE_INPUT__ ' \u0060 \nconsole.log('fixture text');"]];
  const archive=buildZip(files),scope=evaluationScope(archive);
  f.inputs={...f.inputs,archive,currentScope:scope,selection:scope};
  const admission=prepareEvaluationAdmission(f.inputs);
  Object.assign(f.selection,{admissionSha256:admission.admissionSha256,candidateSha256:admission.candidateSha256,
    archiveSha256:admission.archiveSha256});
  const prepared=prepareReviewedCandidateTransport(f.inputs,f.pilot,f.selection);
  const encoded=prepared.source.match(/const INPUT='([^']+)'/)[1];
  const request=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
  assert.equal(createCandidateAssets(request.payloadJson,request.binding).resolve('GET','/app.js').body,files[1][1]);
  assert.equal(prepared.source.replace(encoded,'__CA_FIXED_CANDIDATE_INPUT__'),fixedCandidateBundle().source);
}));

import {previewLocalCandidateTransport} from '../tools/m4/static_web_candidate_transport_bridge.mjs';
test('transport preview CLI requires pinned metadata Docker and does not accept source arguments',()=>{
  const script='tools/m4/static_web_candidate_transport_bridge.mjs';
  const inert=spawnSync(process.execPath,[script],{encoding:'utf8',timeout:5000});
  assert.equal(inert.status,0);assert.match(inert.stdout,/NOT_RUN/);
  const invalid=spawnSync(process.execPath,[script,'--run','project.zip'],{encoding:'utf8',timeout:5000});
  assert.equal(invalid.status,2);assert.equal(JSON.parse(invalid.stdout).reason,'CANDIDATE_TRANSPORT_REQUEST_INVALID');
  const env={...process.env};delete env.CA_EVALUATION_DOCKER_EXE;delete env.CA_EVALUATION_DOCKER_SHA256;
  const blocked=spawnSync(process.execPath,[script,'--stdin'],{env,input:'{}',encoding:'utf8',timeout:5000});
  assert.equal(blocked.status,2);assert.equal(JSON.parse(blocked.stdout).reason,'EVALUATION_DOCKER_PROVENANCE_REQUIRED');
});
test('transport preview rejects nonlocal hosts, arbitrary operations and paths before Docker',()=>{
  const options={platform:'win32',env:{},docker:()=>{throw Error('unexpected Docker call');}};
  assert.throws(()=>previewLocalCandidateTransport({operation:'execute'},options),/REQUEST_INVALID/);
  assert.throws(()=>previewLocalCandidateTransport({}, {...options,env:{DOCKER_HOST:'tcp://remote'}}),/LOCAL_WINDOWS_ONLY/);
  assert.throws(()=>previewLocalCandidateTransport({operation:'prepare-candidate-transport',trustedReceiptName:'../secret',
    receiptNames:[],currentScope:{},criteria:[],reviewedContract:{},archiveBase64:'YQ==',selection:{}},options),/REQUEST_INVALID/);
});

test('read-only transport bridge reconstructs exact data using only metadata commands and selected receipts',()=>withPilotJournal(root=>{
  const env={LOCALAPPDATA:root},journalRoot=path.join(root,'CodingAssistant','pilot-attempts');
  const f=transportFixture(journalRoot);
  const names=Array.from({length:5},(_,i)=>'windows-static-web-'+String(i+1).repeat(12)+'.json');
  const receipts=new Map(names.map((name,i)=>[name,[f.inputs.qualificationReport,f.pilot.working,
    f.pilot.negatives,f.pilot.interruptions,f.pilot.boundary][i]]));
  const calls=[];
  const docker=args=>{
    calls.push(args);
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context'&&args[1]==='inspect')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify({...info,ID:f.pilot.engineId})};
    if(args[0]==='image'&&args[1]==='inspect')return {status:0,stdout:JSON.stringify(f.pilot.imageMetadata)};
    if(args[0]==='ps')return {status:0,stdout:''};
    throw Error('unexpected Docker command');
  };
  const request={operation:'prepare-candidate-transport',trustedReceiptName:names[0],receiptNames:names.slice(1),
    currentScope:f.inputs.currentScope,criteria:f.inputs.criteria,reviewedContract:f.inputs.reviewedContract,
    archiveBase64:f.inputs.archive.toString('base64'),selection:f.selection};
  const result=previewLocalCandidateTransport(request,{platform:'win32',env,docker,readReceipt:name=>receipts.get(name)});
  assert.deepEqual(result,prepareReviewedCandidateTransport(f.inputs,f.pilot,f.selection).descriptor);
  assert.ok(!Object.hasOwn(result,'source')&&!Object.hasOwn(result,'payloadJson'));
  assert.ok(calls.every(args=>['context','info','image','ps'].includes(args[0])));
}));


import {inspectCandidateBuild} from '../tools/m4/static_web_candidate_build.mjs';
import {BUILD_CONTROLS,buildQualifiedCandidateBundle,buildQualificationFixture,buildControlMatches}
  from '../tools/m4/static_web_build_qualification.mjs';
function inspectBuildFixture(name){
  const bundle=buildQualifiedCandidateBundle(),fixture=buildQualificationFixture(name,bundle,'a'.repeat(64),'b'.repeat(32));
  const encoded=fixture.source.match(/const INPUT='([^']+)'/)[1];
  const data=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
  return {assets:createCandidateAssets(data.payloadJson,data.binding),files:data.binding.files,fixture,bundle};
}
test('restricted build accepts working and spoof control markup, blocks broken and missing assets before browser',()=>{
  for(const name of BUILD_CONTROLS){
    const {assets,files}=inspectBuildFixture(name);
    const result=inspectCandidateBuild(assets,files,content=>content!=='return 1;');
    assert.deepEqual(result,{javascript_syntax:name!=='build-invalid-syntax',
      local_assets_linked:['build-working','build-spoof','build-invalid-syntax'].includes(name)},name);
  }
});
test('restricted build rejects unsupported tags, duplicate attributes, ambiguous references and inline scripts',()=>{
  const html=src=>'<!doctype html><html><head><title>Test</title></head><body>'+src+'</body></html>';
  const inspect=(markup,more=[])=>{
    const files=[['index.html',markup],['app.js','const n=1;'],...more];
    const assets={resolve:(method,target)=>({status:200,body:files.find(([name])=>'/'+name===target)[1]})};
    return inspectCandidateBuild(assets,files.map(([path])=>({path})),()=>true);
  };
  const script='<script src="./app.js" defer></script>';
  assert.equal(inspect(html(script)).local_assets_linked,true);
  for(const body of ['<iframe></iframe>','<script>alert(1)</script>','<script src=app.js></script>',
    '<script src="app.js" SRC="other.js"></script>','<script src="app.js?x=1"></script>',
    '<script src="%61pp.js"></script>','<script type="module" src="app.js"></script>',
    '<div onclick="x()"></div>'+script,'<div id="x"></div><span id="x"></span>'+script,
    '<base href="/">'+script,'<style>@import "other.css";</style>'+script])
    assert.equal(inspect(html(body)).local_assets_linked,false,body);
  assert.equal(inspect(html(script),[['extra.js','const m=2;']]).local_assets_linked,false);
  assert.equal(inspect(html(script),[['styles.css','body{}']]).local_assets_linked,false);
  assert.equal(inspect(html(script),[['data.json','invalid']]).local_assets_linked,false);
  assert.equal(inspect(html('<link rel="stylesheet" href="./styles.css">'+script),[['styles.css','body{}']]).local_assets_linked,true);
});
test('build qualification has distinct adapter digest and checks exact expected observations',()=>{
  const {bundle}=inspectBuildFixture('build-working');
  assert.notEqual(bundle.adapterSha256,fixedCandidateBundle().adapterSha256);
  assert.equal(bundle.evaluatorSha256,fixedCandidateBundle().evaluatorSha256);
  const {report,expected}=candidateObservationFixture();
  assert.equal(buildControlMatches('build-working',parseCandidateObservations(JSON.stringify(report),expected)),true);
  for(const name of ['build-broken','build-missing-asset','build-invalid-syntax']){
    const failed=structuredClone(report);
    failed.build.javascript_syntax=name!=='build-invalid-syntax';
    failed.build.local_assets_linked=name==='build-invalid-syntax';
    failed.infrastructure=allCases(Object.keys(failed.infrastructure),null);
    failed.cases=allCases(CALCULATION_CASES,null);
    const o=parseCandidateObservations(JSON.stringify(failed),expected);
    assert.equal(buildControlMatches(name,o),true);
    assert.equal(buildControlMatches(name,{...o,completed:1}),false);
    assert.equal(buildControlMatches(name,{...o,infrastructure:{...o.infrastructure,browser_closed:true}}),false);
  }
});


import {bindBuildQualifiedPilotEvidence} from '../tools/m4/static_web_pilot_evidence.mjs';
function buildEvidenceFixture(root){
  const input=completePilotEvidenceFixture(root),bundle=buildQualifiedCandidateBundle();
  const policy=preparePilotPolicy(input.imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  const controls={};
  for(const [i,name] of BUILD_CONTROLS.entries()){
    const attemptId=(i+7).toString(16).padStart(32,'0'),fixture=buildQualificationFixture(name,bundle,policy.policySha256,attemptId);
    const j=reservePilotAttempt(root,{...policy.identity(attemptId),engineId:input.engineId,
      candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256});
    j.advance('Creating');j.advance('Created',attemptId.repeat(2));j.advance('Audited');j.advance('Starting');
    j.advance('Exited');j.advance('CleanupPending');j.advance('Cleaned');j.archive();
    const {report}=candidateObservationFixture();Object.assign(report,fixture.expected);
    if(name==='build-spoof')report.cases=allCases(CALCULATION_CASES,false);
    if(!['build-working','build-spoof'].includes(name)){
      report.build.javascript_syntax=name!=='build-invalid-syntax';
      report.build.local_assets_linked=name==='build-invalid-syntax';
      report.infrastructure=allCases(Object.keys(report.infrastructure),null);
      report.cases=allCases(CALCULATION_CASES,null);
    }
    controls[name]={...input.working,status:'PILOT_BUILD_CONTROL_PASS',attemptId,policySha256:policy.policySha256,
      observations:parseCandidateObservations(JSON.stringify(report),fixture.expected)};
  }
  input.build={...input.negatives,stage:'AET-8-pilot-build-controls',status:'PILOT_BUILD_CONTROLS_PASS',controls};
  return input;
}
test('build-qualified evidence binds eleven distinct attempts and preserves historical digests',()=>withPilotJournal(root=>{
  const input=buildEvidenceFixture(root),prior=bindCompletePilotEvidence(input),upgraded=bindBuildQualifiedPilotEvidence(input);
  assert.equal(upgraded.status,'PILOT_BUILD_QUALIFIED_HISTORICAL_EVIDENCE_BOUND');
  assert.equal(upgraded.completeEvidenceSha256,prior.completeEvidenceSha256);
  assert.notEqual(upgraded.buildQualification.policySha256,prior.policySha256);
  assert.equal(Object.keys(upgraded.buildQualification.attempts).length,5);
  assert.equal(new Set([...Object.values(upgraded.attempts),upgraded.boundary,...Object.values(upgraded.buildQualification.attempts)].map(x=>x.attemptId)).size,11);
  assert.match(upgraded.qualifiedEvidenceSha256,/^[a-f0-9]{64}$/);
  assert.equal(upgraded.executionEnabled,false);assert.equal(upgraded.runtimeQualified,false);
}));
test('build evidence rejects replay, missing controls, wrong policy and forged outcomes',()=>withPilotJournal(root=>{
  const input=buildEvidenceFixture(root);
  for(const change of [
    x=>{delete x.build.controls['build-broken'];},
    x=>{x.build.controls['build-working'].attemptId=x.working.attemptId;},
    x=>{x.build.controls['build-spoof'].policySha256=x.working.policySha256;},
    x=>{x.build.controls['build-invalid-syntax'].observations.build.javascript_syntax=true;},
    x=>{x.build.controls['build-missing-asset'].observations.infrastructure.browser_closed=true;},
    x=>{x.build.controls['build-working'].cleanupDebt=true;}]){
    const changed=structuredClone(input);change(changed);assert.throws(()=>bindBuildQualifiedPilotEvidence(changed));
  }
}));
test('upgraded transport binds exact new build adapter and rejects old policy or evidence substitution',()=>withPilotJournal(root=>{
  const pilot=buildEvidenceFixture(root),inputs=admissionFixture(),admission=prepareEvaluationAdmission(inputs);
  const evidence=bindBuildQualifiedPilotEvidence(pilot),bundle=buildQualifiedCandidateBundle();
  const selection={attemptId:'e'.repeat(32),admissionSha256:admission.admissionSha256,candidateSha256:admission.candidateSha256,
    archiveSha256:admission.archiveSha256,completeEvidenceSha256:evidence.qualifiedEvidenceSha256,
    policySha256:evidence.buildQualification.policySha256,adapterSha256:bundle.adapterSha256};
  const result=prepareReviewedCandidateTransport(inputs,pilot,selection);
  const encoded=result.source.match(/const INPUT='([^']+)'/)[1];
  assert.equal(result.source.replace(encoded,'__CA_FIXED_CANDIDATE_INPUT__'),bundle.source);
  assert.equal(result.descriptor.executionEnabled,false);
  assert.throws(()=>prepareReviewedCandidateTransport(inputs,pilot,{...selection,policySha256:evidence.policySha256}),/BINDING_CHANGED/);
  assert.throws(()=>prepareReviewedCandidateTransport(inputs,pilot,{...selection,completeEvidenceSha256:evidence.completeEvidenceSha256}),/BINDING_CHANGED/);
  assert.throws(()=>prepareReviewedCandidateTransport(inputs,pilot,{...selection,attemptId:pilot.build.controls['build-working'].attemptId}),/ATTEMPT_REUSED/);
}));


import {qualifyPinnedDispatch,strictDispatchObservations} from '../tools/m4/windows_pinned_dispatch_qualification.mjs';
function pinnedDispatchFixture(options={}){
  const f=durablePilotDocker(options);
  const docker=(args,timeout,source)=>{
    const reply=f.docker(args,timeout,source);
    if(args[0]==='start'&&reply.status===0)
      reply.stdout=['START','SECURITY','BUILD','SANDBOX'].map(stage=>JSON.stringify({kind:'progress',stage})).join('\n')+'\n'+reply.stdout;
    return reply;
  };
  return {...f,docker,env:{CA_PILOT_NODE_SHA256:'a'.repeat(64),CA_PILOT_DOCKER_SHA256:'b'.repeat(64)}};
}
test('pinned dispatcher fixed control enforces audited exact input and rejects archived attempt replay',()=>withPilotJournal(root=>{
  const f=pinnedDispatchFixture();
  const result=qualifyPinnedDispatch({platform:'win32',env:f.env,docker:f.docker,root,attemptId:f.attempt});
  assert.equal(result.status,'PILOT_PINNED_DISPATCH_PASS',result.reason);
  assert.equal(result.replayBlocked,true);assert.equal(result.containerRemoved,true);
  assert.equal(result.cleanupDebt,false);assert.equal(result.generatedCodeExecutionEnabled,false);
  assert.equal(f.calls.filter(a=>a[0]==='create').length,1);
  assert.equal(f.calls.filter(a=>a[0]==='start').length,1);
}));
test('pinned dispatch fails closed on incomplete stream, ownership drift or cleanup failure',()=>{
  for(const options of [{badExit:true},{wrongOwner:true},{cleanupFails:true},{negative:true}]){
    withPilotJournal(root=>{
      const f=pinnedDispatchFixture(options);
      const result=qualifyPinnedDispatch({platform:'win32',env:f.env,docker:f.docker,root,attemptId:f.attempt});
      assert.equal(result.status,'BLOCKED',JSON.stringify(options));assert.equal(result.replayBlocked,false);
      if(options.cleanupFails||options.wrongOwner)assert.equal(result.cleanupDebt,true);
    });
  }
  const {report,expected}=candidateObservationFixture();
  const progress=stage=>JSON.stringify({kind:'progress',stage});
  const good=['START','SECURITY','BUILD','SANDBOX','COMPLETE'].map(progress);
  assert.equal(strictDispatchObservations([...good,JSON.stringify(report)].join('\n'),expected).passed,7);
  for(const lines of [good.slice(1),[...good,progress('COMPLETE')],[good[1],good[0],...good.slice(2)]])
    assert.throws(()=>strictDispatchObservations([...lines,JSON.stringify(report)].join('\n'),expected),/STREAM_INVALID/);
});
test('pinned dispatcher CLI is inert and accepts no project input',()=>{
  const script='tools/m4/windows_pinned_dispatch_qualification.mjs';
  const inert=spawnSync(process.execPath,[script],{encoding:'utf8',timeout:5000});
  assert.equal(inert.status,0);assert.match(inert.stdout,/NOT_RUN/);
  const invalid=spawnSync(process.execPath,[script,'--run-fixed-control','project.zip'],{encoding:'utf8',timeout:5000});
  assert.equal(invalid.status,2);
});
