#!/usr/bin/env node
// AET-8 fixed trusted integrated qualification. No generated archive/path,
// no execution API, and no authorization to reuse this profile for candidates.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {IMAGE_TAG,validateDockerContext,validatePinnedImage,shellDocker,writePrivateEvidence} from './windows_docker_qualification.mjs';
import {checkedChromiumSeccomp,sandboxDockerArgs,FIXTURE_PID_LIMIT} from './windows_chromium_sandbox_qualification.mjs';
import {readPinnedSources} from './windows_calculator_evaluator_qualification.mjs';
import {CALCULATION_CASES,CALCULATION_CONTRACT_VERSION} from './calculation_journal_contract.mjs';
import {SECURITY_CASES,FIXTURE_CASES,FAILURE_STAGES,FAILURE_CODES} from './windows_aet8_trusted_guest.mjs';

const NAME=/^ca-win-aet8-[0-9a-f]{12}$/;
const read=name=>fs.readFileSync(new URL(name,import.meta.url),'utf8').replace(/\r\n/g,'\n');
const guest=read('./windows_aet8_trusted_guest.mjs');
const contract=read('./calculation_journal_contract.mjs');
const driver=read('./windows_aet8_pipe_driver.mjs');
const sandbox=read('./windows_chromium_sandbox_guest.mjs');
const sha=x=>createHash('sha256').update(x).digest('hex');
const b64=x=>Buffer.from(x).toString('base64');
const fail=code=>{throw Error(code);};
export function trustedDockerArgs(id,name){
  if(!NAME.test(name))fail('INVALID_FIXED_QUALIFICATION_NAME');
  const m47='ca-win-m47-'+name.slice(-12);
  const args=sandboxDockerArgs(id,m47).map(a=>a===m47?name:a==='codingassistant.m47='+m47?'codingassistant.aet8='+name:a==='--run-fixed-sandbox-probe'?'--run-trusted-aet8':a);
  // Docker's private init reaps orphaned Chromium descendants; no host PID
  // namespace or additional capabilities. Only this fixed integrated guest.
  args.splice(1,0,'--init');return args;
}
function booleans(x,keys){return x&&typeof x==='object'&&!Array.isArray(x)&&Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k)&&typeof x[k]==='boolean');}
export function parseTrustedReport(raw){
  if(typeof raw!=='string'||raw.length<2||raw.length>10000)fail('GUEST_REPORT_INVALID');
  let x;try{x=JSON.parse(raw);}catch{fail('GUEST_REPORT_INVALID');}
  const failure=x?.failure;
  const validFailure=failure&&Object.keys(failure).length===2&&FAILURE_STAGES.includes(failure.stage)&&FAILURE_CODES.includes(failure.code);
  if(!x||Object.keys(x).length!==(failure?5:4)||(failure&&!validFailure)||x.schemaVersion!==1||
    !(booleans(x.security,SECURITY_CASES)||(validFailure&&x.security===null))||!booleans(x.fixture,FIXTURE_CASES)||
    !x.observations||Object.keys(x.observations).length!==3||!['working','broken','spoof'].every(k=>Object.hasOwn(x.observations,k)&&(x.observations[k]===null||booleans(x.observations[k],CALCULATION_CASES))))
    fail('GUEST_REPORT_INVALID');
  const all=(name,want)=>CALCULATION_CASES.every(k=>x.observations[name]?.[k]===want);
  if(x.fixture.working_seven_checks!==all('working',true)||x.fixture.inert_negative_rejected!==all('broken',false)||x.fixture.forged_receipt_rejected!==all('spoof',false)||
    (x.fixture.same_guest_sandbox_compatibility&&!SECURITY_CASES.every(k=>x.security?.[k]===true))||
    (!x.fixture.same_guest_sandbox_compatibility&&Object.values(x.observations).some(v=>v!==null)))
    fail('GUEST_REPORT_CONTRADICTORY');
  return x;
}
// Bounded checkpoints are diagnostic-only. A complete final record with
// a zero guest exit and confirmed cleanup remains mandatory for a pass.
export function parseGuestStream(raw){
  if(typeof raw!=='string'||raw.length>60000)fail('GUEST_REPORT_INVALID');
  const lines=raw.trim().split(/\r?\n/).filter(Boolean);
  if(lines.length>64)fail('GUEST_REPORT_INVALID');
  let checkpoint=null,final=null;
  for(const line of lines){
    let x;try{x=JSON.parse(line);}catch{fail('GUEST_REPORT_INVALID');}
    if(x?.kind==='progress'){
      if(final||Object.keys(x).length!==6||!FAILURE_STAGES.includes(x.stage))fail('GUEST_REPORT_INVALID');
      const {kind,stage,...data}=x;
      const safe=parseTrustedReport(JSON.stringify({...data,failure:{stage,code:'GUEST_DEADLINE_EXCEEDED'}}));
      checkpoint={stage,security:safe.security,fixture:safe.fixture,observations:safe.observations};
    }else{
      if(final)fail('GUEST_REPORT_INVALID');
      final=parseTrustedReport(line);
    }
  }
  return {checkpoint,final};
}
export function trustedPayload(){
  const sources=readPinnedSources();
  const hashes=Object.fromEntries(Object.entries(sources).map(([k,v])=>[k,sha(v)]));
  const payload={sources:Object.fromEntries(Object.entries(sources).map(([k,v])=>[k,b64(v)])),hashes,
    sandbox:b64(sandbox),contract:b64(contract),driver:b64(driver)};
  const marker='__CA_AET8_FIXED_PAYLOAD__';
  if(guest.split(marker).length!==2)fail('FIXED_GUEST_MARKER_INVALID');
  return {source:guest.replace(marker,b64(JSON.stringify(payload))),hashes};
}
export function qualifyTrusted({platform=process.platform,env=process.env,docker=shellDocker,name='ca-win-aet8-'+randomBytes(6).toString('hex')}={}){
  const report={schemaVersion:1,stage:'AET-8-trusted-integrated-calculation',status:'BLOCKED',recordedAtUtc:new Date().toISOString(),
    imageTag:IMAGE_TAG,imageId:null,registryDigest:null,seccompSha256:null,fixturePidLimit:FIXTURE_PID_LIMIT,
    evaluatorVersion:CALCULATION_CONTRACT_VERSION,evaluatorSha256:sha(contract),driverSha256:sha(driver),guestSha256:null,
    trustedSourceHashes:null,security:null,fixture:null,observations:null,containerRemoved:false,
    generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false,reason:null,guestFailure:null,lastGuestStage:null};
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT||!NAME.test(name)){report.reason='LOCAL_WINDOWS_ONLY';return report;}
  let attempted=false;
  const json=args=>{const x=docker(args,9000);if(x.status!==0||!x.stdout)fail('DOCKER_METADATA_UNAVAILABLE');try{return JSON.parse(x.stdout);}catch{fail('DOCKER_METADATA_INVALID');}};
  const absent=()=>{const x=docker(['ps','-a','--filter','name=^/'+name+'$','--format','{{.ID}}'],15000);return x.status===0&&x.stdout.trim()==='';};
  try{
    const payload=trustedPayload();report.guestSha256=sha(payload.source);report.trustedSourceHashes=payload.hashes;
    const context=docker(['context','show'],8000);
    if(context.status!==0||!context.stdout)fail('DOCKER_CONTEXT_UNAVAILABLE');
    const current=context.stdout.trim();
    validateDockerContext(current,json(['context','inspect',current,'--format','{{json .}}'])?.Endpoints?.docker?.Host,json(['info','--format','{{json .}}']));
    const pin=validatePinnedImage(json(['image','inspect',IMAGE_TAG,'--format','{{json .}}']));
    report.imageId=pin.imageId;report.registryDigest=pin.registryDigest;report.seccompSha256=checkedChromiumSeccomp().sha256;
    if(!absent())fail('DISPOSABLE_NAME_COLLISION');
    attempted=true;
    const execution=docker(trustedDockerArgs(pin.imageId,name),180000,payload.source);
    const {checkpoint,final}=parseGuestStream(execution.stdout||'');
    if(checkpoint){report.lastGuestStage=checkpoint.stage;report.security=checkpoint.security;report.fixture=checkpoint.fixture;report.observations=checkpoint.observations;}
    if(execution.timedOut)fail('FIXED_EVALUATOR_TIMEOUT');
    if(!final)fail('GUEST_REPORT_INVALID');
    const x=final;
    report.security=x.security;report.fixture=x.fixture;report.observations=x.observations;report.guestFailure=x.failure??null;
    if(x.failure)fail('GUEST_STAGE_BLOCKED');
    if(execution.status!==0||!Object.values(x.security).every(Boolean)||!Object.values(x.fixture).every(Boolean))fail('FIXED_EVALUATOR_CHECKS_FAILED');
    report.status='TRUSTED_INTEGRATED_EVALUATOR_PASS';
  }catch(e){report.reason=/^[A-Z_]{3,80}$/.test(e?.message??'')?e.message:'QUALIFICATION_BLOCKED';}
  finally{
    if(attempted){
      try{
        const found=docker(['container','inspect',name,'--format','{{json .Config.Labels}}'],15000);
        if(found.status===0){
          if(JSON.parse(found.stdout)?.['codingassistant.aet8']!==name)fail('CONTAINER_OWNERSHIP_MISMATCH');
          docker(['rm','-f',name],15000);
        }
        report.containerRemoved=absent();
      }catch{report.containerRemoved=false;}
      if(!report.containerRemoved){report.status='BLOCKED';report.reason='CONTAINER_CLEANUP_UNCONFIRMED';}
    }
  }
  return report;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 TRUSTED_INTEGRATED: NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run'){console.error('Usage: node tools/m4/windows_aet8_trusted_qualification.mjs --run');return 2;}
  const r=qualifyTrusted();
  console.log('AET-8 WINDOWS_'+r.status);
  for(const [name,values] of [['security',r.security],['integrated fixture',r.fixture]]){
    console.log('  '+name+': '+(values?Object.values(values).filter(Boolean).length+'/'+Object.keys(values).length:'NOT_RUN'));
    if(values)for(const [key,value] of Object.entries(values))if(!value)console.log('    failed: '+key);
  }
  if(r.observations)for(const [name,values] of Object.entries(r.observations)){
    console.log('  '+name+': '+(values?Object.values(values).filter(Boolean).length+'/'+CALCULATION_CASES.length:'NOT_RUN'));
    if(values)for(const key of CALCULATION_CASES)if(values[key]!== (name==='working'))console.log('    unexpected: '+key);
  }
  console.log('  container removed: '+r.containerRemoved);
  if(r.lastGuestStage)console.log('  last guest stage: '+r.lastGuestStage);
  if(r.guestFailure)console.log('  guest failure: '+r.guestFailure.stage+' / '+r.guestFailure.code);
  if(r.reason)console.log('  blocker: '+r.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(r));
  return r.status==='TRUSTED_INTEGRATED_EVALUATOR_PASS'&&r.containerRemoved?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
