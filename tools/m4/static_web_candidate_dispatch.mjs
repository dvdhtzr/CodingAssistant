#!/usr/bin/env node
// Shared candidate lifecycle. No generated-source execution authority.
// Live entry is restricted to frozen qualification controls. The reviewed
// candidate entry reconstructs exact source, then fails closed until the
// separate executable grant and host-process ownership policy are ready.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {pinnedDispatchDocker} from './windows_pinned_dispatch_qualification.mjs';
import {pilotHostTools,pilotLocalRoot,assertPilotContainerOwned} from './windows_pilot_lifecycle_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {buildQualifiedCandidateBundle,buildQualificationFixture,buildControlMatches,BUILD_CONTROLS}
  from './static_web_build_qualification.mjs';
import {prepareReviewedCandidateTransport} from './static_web_candidate_transport.mjs';
import {reservePilotAttempt,readPilotAttempt,readArchivedPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {parseCandidateObservations} from './static_web_candidate_observations.mjs';
import {writePrivateEvidence} from './windows_docker_qualification.mjs';
const fail=code=>{throw Error(code);};
const sha=value=>createHash('sha256').update(value).digest('hex');
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const equal=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const label='codingassistant.static-web-pilot-qualification';
const goodStages=['Reserved','Creating','Created','Audited','Starting','Exited','CleanupPending','Cleaned'];
const reason=e=>/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'CANDIDATE_DISPATCH_BLOCKED';

export function parseCandidateDispatchStream(raw,expected){
  if(typeof raw!=='string'||Buffer.byteLength(raw)>32000)fail('CANDIDATE_DISPATCH_STREAM_INVALID');
  const lines=raw.trimEnd().split(/\r?\n/);
  const observed=parseCandidateObservations(lines.at(-1),expected);
  if(!['BUILD_FAILED','TESTS_FAILED','CHECKS_PASSED_NOT_VERIFIED'].includes(observed.status))
    fail('CANDIDATE_DISPATCH_OBSERVATIONS_BLOCKED');
  const stages=observed.status==='BUILD_FAILED'?['START','SECURITY','BUILD','COMPLETE']:
    ['START','SECURITY','BUILD','SANDBOX','COMPLETE'];
  if(lines.length!==stages.length+1||stages.some((stage,i)=>{
    try{const p=JSON.parse(lines[i]);return Object.keys(p).length!==2||p.kind!=='progress'||p.stage!==stage;}
    catch{return true;}
  }))fail('CANDIDATE_DISPATCH_STREAM_INVALID');
  if(observed.status==='BUILD_FAILED'&&Object.values(observed.infrastructure).some(v=>v!==null))
    fail('CANDIDATE_DISPATCH_STREAM_INVALID');
  return observed;
}
// The exact candidate can be constructed only from reviewed admission,
// archives and the complete fourteen-attempt evidence, never from a
// supplied guest program or a caller's claimed pass.
function reconstructReservedTransport(inputs,pilotInputs,selection,dispatchPins,reservedDescriptor){
  if(!pilotInputs.dispatch||!pilotInputs.pinnedInterruptions)fail('PILOT_DISPATCH_EVIDENCE_REQUIRED');
  const prepared=prepareReviewedCandidateTransport(inputs,pilotInputs,selection,dispatchPins);
  if(!equal(prepared.descriptor,reservedDescriptor))fail('CANDIDATE_DISPATCH_RESERVATION_CHANGED');
  return prepared;
}
export function prepareCandidateDispatch(inputs,pilotInputs,selection,dispatchPins,reservedDescriptor){
  return reconstructReservedTransport(inputs,pilotInputs,selection,dispatchPins,reservedDescriptor).descriptor;
}
function requireGeneratedExecutionGrant(){
  // No injectable boolean, callback or environment variable can open
  // this gate. The later .NET consumed grant and Windows process owner
  // must be implemented and separately qualified first.
  fail('EVALUATION_EXECUTION_ADAPTER_NOT_QUALIFIED');
}
export async function dispatchReviewedCandidate(inputs,pilotInputs,selection,dispatchPins,reservedDescriptor,signal){
  const transport=reconstructReservedTransport(inputs,pilotInputs,selection,dispatchPins,reservedDescriptor);
  requireGeneratedExecutionGrant();
  // Intentionally unreachable until the closed grant gate is replaced
  // by a qualified host-owned admission and launch implementation.
  // The same lifecycle consumes the exact reconstructed data envelope.
  const env=process.env,root=pilotLocalRoot(env),docker=pinnedDispatchDocker(env);
  const host=pilotHostTools(docker),engineId=host.engine();
  const image=readLocalImage({env,docker}),bundle=buildQualifiedCandidateBundle();
  const policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
  if(root!==pilotInputs.root||engineId!==transport.descriptor.engineId||
    policy.policySha256!==transport.descriptor.policySha256||
    dispatchPins.nodeSha256!==env.CA_PILOT_NODE_SHA256||dispatchPins.dockerSha256!==env.CA_PILOT_DOCKER_SHA256)
    fail('CANDIDATE_DISPATCH_BINDING_CHANGED');
  return executeTransport({transport,policy,root,engineId,docker,
    attach:(id,source,token)=>pinnedAttach(id,source,env,token),signal});
}

function requireBinary(file,hash,base){
  if(typeof file!=='string'||!path.isAbsolute(file)||!digest(hash)||
    path.basename(file).toLowerCase()!==base)fail('PILOT_DISPATCH_PIN_REQUIRED');
  try{
    if(!fs.lstatSync(file).isFile()||fs.realpathSync(file).toLowerCase()!==path.resolve(file).toLowerCase()||
      sha(fs.readFileSync(file))!==hash)fail('PILOT_DISPATCH_PIN_CHANGED');
  }catch{fail('PILOT_DISPATCH_PIN_CHANGED');}
}
// Track the actual Docker attach child through 'close' (process exit
// and stdio closed), not simply a request to kill it. If close cannot
// be confirmed the journal remains active; do not remove/replay work
// while the attach request may still be in flight.
async function pinnedAttach(id,source,env,signal){
  if(process.platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT||!digest(id))
    fail('PILOT_LOCAL_WINDOWS_ONLY');
  const check=()=>{
    requireBinary(process.execPath,env.CA_PILOT_NODE_SHA256,'node.exe');
    requireBinary(env.CA_PILOT_DOCKER_EXE,env.CA_PILOT_DOCKER_SHA256,'docker.exe');
  };
  check();
  if(signal?.aborted)return {closed:true,status:null,stdout:'',interrupted:true};
  const result=await new Promise(resolve=>{
    let child,settled=false,closed=false,interrupted=false,failed=false,total=0;
    let deadline,killDeadline;
    const chunks=[];
    const finish=value=>{
      if(settled)return;
      settled=true;clearTimeout(deadline);clearTimeout(killDeadline);
      signal?.removeEventListener('abort',stop);
      resolve(value);
    };
    function stop(){
      if(settled||closed||interrupted)return;
      interrupted=true;
      try{child.kill('SIGKILL');}catch{}
      killDeadline=setTimeout(()=>finish({closed:false,status:null,stdout:'',interrupted:true}),10000);
    }
    try{
      child=spawn(env.CA_PILOT_DOCKER_EXE,['start','-a','-i',id],
        {env,windowsHide:true,shell:false,stdio:['pipe','pipe','pipe']});
      child.once('error',()=>{failed=true;stop();});
      child.stdout.on('data',chunk=>{
        total+=chunk.length;
        if(total>32000){failed=true;stop();return;}
        chunks.push(chunk);
      });
      child.stderr.on('data',chunk=>{total+=chunk.length;if(total>65536){failed=true;stop();}});
      child.stdin.on('error',()=>{failed=true;stop();});
      child.once('close',(code,exitSignal)=>{
        closed=true;finish({closed:true,status:failed||exitSignal?null:code,
          stdout:failed||interrupted?'':Buffer.concat(chunks).toString('utf8'),interrupted});
      });
      deadline=setTimeout(stop,180000);
      signal?.addEventListener('abort',stop,{once:true});
      if(signal?.aborted)stop();else child.stdin.end(source,'utf8');
    }catch{
      // Spawn failed before a child was returned, or stdio setup failed.
      if(!child)finish({closed:true,status:null,stdout:'',interrupted:false});
      else{failed=true;stop();}
    }
  });
  // Preserve unconfirmed close even if the subsequent pin check fails.
  if(!result.closed)return result;
  try{check();}catch{return {...result,status:null,stdout:''};}
  return result;
}

async function executeTransport({transport,policy,root,engineId,docker,attach,signal}){
  const {expected,source}=transport;
  const attemptId=expected.attemptId;
  const result={schemaVersion:1,stage:'AET-8-candidate-dispatch-lifecycle',status:'VERIFICATION_BLOCKED',
    attemptId,engineId,policySha256:policy.policySha256,transportSha256:sha(source),
    configurationAudited:false,attachClosed:false,containerRemoved:false,cleanupDebt:false,
    archiveSha256:null,observations:null,reason:null,
    executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false};
  let journal,id=null,attachStarted=false,attachClosed=false,observed=null;
  const host=pilotHostTools(docker),identity={...policy.identity(attemptId),engineId,
    candidateSha256:expected.candidateSha256,archiveSha256:expected.archiveSha256};
  try{
    if(signal?.aborted)fail('CANDIDATE_DISPATCH_INTERRUPTED');
    if(expected.policySha256!==policy.policySha256||expected.adapterSha256!==policy.descriptor.adapterSha256||
      expected.evaluatorSha256!==policy.descriptor.evaluatorSha256||
      typeof source!=='string'||Buffer.byteLength(source)>4*1024*1024)
      fail('CANDIDATE_DISPATCH_BINDING_CHANGED');
    if(readPilotAttempt(root)||host.engine()!==engineId||host.list('label='+label)!=='')
      fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    journal=reservePilotAttempt(root,identity);
    if(host.engine()!==engineId||host.list('label='+label)!==''||host.list('name=^/'+identity.name+'$')!=='')
      fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    if(signal?.aborted)fail('CANDIDATE_DISPATCH_INTERRUPTED');
    journal.advance('Creating');
    id=host.query(policy.createArgs(attemptId),30000);
    if(!digest(id)){id=null;fail('PILOT_CONTAINER_ID_INVALID');}
    journal.advance('Created',id);
    policy.audit(attemptId,id,host.inspect(id));journal.advance('Audited');result.configurationAudited=true;
    if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
    policy.audit(attemptId,id,host.inspect(id));
    if(signal?.aborted)fail('CANDIDATE_DISPATCH_INTERRUPTED');
    journal.advance('Starting');
    attachStarted=true;
    const child=await attach(id,source,signal);
    attachClosed=child?.closed===true;result.attachClosed=attachClosed;
    if(!attachClosed)fail('CANDIDATE_DISPATCH_ATTACH_UNCONFIRMED');
    if(child.interrupted||signal?.aborted)fail('CANDIDATE_DISPATCH_INTERRUPTED');
    if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
    const actual=host.inspect(id);assertPilotContainerOwned(identity,id,actual);
    if(child.status!==0||actual.State?.Running!==false||actual.State?.Status!=='exited'||actual.State?.ExitCode!==0)
      fail('PILOT_GUEST_EXIT_UNCONFIRMED');
    journal.advance('Exited');
    observed=parseCandidateDispatchStream(child.stdout,expected);
  }catch(e){result.reason=reason(e);}
  finally{
    if(journal)try{
      if(attachStarted&&!attachClosed)fail('CANDIDATE_DISPATCH_ATTACH_UNCONFIRMED');
      if(journal.current.stage==='Reserved'){
        journal.advance('CleanupPending');journal.advance('Cleaned');journal.archive();
      }else{
        // Never guess the outcome of a create without an exact durable ID.
        if(!id||journal.current.containerId!==id)fail('PILOT_CREATE_OUTCOME_UNCERTAIN');
        if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
        const found=host.list('name=^/'+identity.name+'$');
        if(found!==id)fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
        assertPilotContainerOwned(identity,id,host.inspect(id));
        journal.advance('CleanupPending');
        if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
        assertPilotContainerOwned(identity,id,host.inspect(id));
        if(host.query(['rm','-f',id],15000)!==id)fail('PILOT_CLEANUP_UNCONFIRMED');
        if(host.engine()!==engineId||host.list('name=^/'+identity.name+'$')!==''||
          host.list('id='+id)!==''||host.list('label='+label)!=='')
          fail('PILOT_CLEANUP_UNCONFIRMED');
        journal.advance('Cleaned');journal.archive();result.containerRemoved=true;
      }
    }catch(e){result.reason=reason(e);result.cleanupDebt=true;}
    try{
      if(readPilotAttempt(root)!==null||host.engine()!==engineId||host.list('label='+label)!=='')
        result.cleanupDebt=true;
    }catch{result.cleanupDebt=true;}
  }
  if(!result.reason&&!result.cleanupDebt&&result.configurationAudited&&result.attachClosed&&result.containerRemoved&&observed){
    try{
      const archive=readArchivedPilotAttempt(root,attemptId);
      if(!equal(archive.identity,identity)||!equal(archive.events.map(e=>e.stage),goodStages)||
        archive.recoveryClaimed||archive.events.at(-1).containerId!==id)
        fail('CANDIDATE_DISPATCH_ARCHIVE_CHANGED');
      result.archiveSha256=sha(JSON.stringify(archive));
      // Only now expose functional observations. Never a VERIFIED claim.
      result.observations=observed;result.status=observed.status;
    }catch(e){result.reason=reason(e);}
  }
  return result;
}

// Frozen controls exercise the *new* shared lifecycle with the unchanged
// build-qualified guest. There is no CLI for arbitrary source, archives,
// commands or grant data. Injection is for model-free fixed-fixture tests.
export async function qualifyCandidateDispatchControl({platform=process.platform,env=process.env,docker,attach,root,
  attemptId=randomBytes(16).toString('hex'),control='build-working',signal}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('PILOT_LOCAL_WINDOWS_ONLY');
  if(!BUILD_CONTROLS.includes(control))fail('PILOT_FIXED_CONTROL_REQUIRED');
  docker??=pinnedDispatchDocker(env);
  attach??=(id,source,token)=>pinnedAttach(id,source,env,token);
  root??=pilotLocalRoot(env);
  const host=pilotHostTools(docker),engineId=host.engine(),image=readLocalImage({platform,env,docker});
  const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
  const transport=buildQualificationFixture(control,bundle,policy.policySha256,attemptId);
  const result=await executeTransport({transport,policy,root,engineId,docker,attach,signal});
  return {...result,control,qualificationPassed:!result.cleanupDebt&&result.attachClosed&&result.containerRemoved&&
    buildControlMatches(control,result.observations)};
}
export async function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('CANDIDATE_DISPATCH_LIFECYCLE NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run-fixed-controls'){console.error('Only --run-fixed-controls is supported');return 2;}
  const report={schemaVersion:1,stage:'AET-8-candidate-dispatch-lifecycle-controls',status:'BLOCKED',
    recordedAtUtc:new Date().toISOString(),controls:{},generatedSourceExecuted:false,
    generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false,reason:null};
  try{
    if(Object.keys(process.env).some(k=>/^NODE_|^OPENSSL_(CONF|MODULES)$/i.test(k)&&process.env[k]))
      fail('PILOT_DISPATCH_ENVIRONMENT_OVERRIDE');
    for(const control of ['build-working','build-spoof','build-missing-asset']){
      const result=await qualifyCandidateDispatchControl({control});
      report.controls[control]=result;
      console.log('  '+control+': '+result.status+'; attach closed: '+result.attachClosed+
        '; removed: '+result.containerRemoved+'; debt: '+result.cleanupDebt);
      if(!result.qualificationPassed)fail(result.reason??'CANDIDATE_DISPATCH_CONTROL_FAILED');
    }
    const values=Object.values(report.controls);
    if(values.some(v=>v.engineId!==values[0].engineId||v.policySha256!==values[0].policySha256))
      fail('PILOT_DISPATCH_HOST_CHANGED');
    report.status='PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS';
  }catch(e){report.reason=reason(e);}
  console.log('AET-8 WINDOWS_'+report.status);
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(report));
  return report.status==='PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=await main();
