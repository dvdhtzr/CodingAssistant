#!/usr/bin/env node
// Qualification ONLY: fixed trusted candidate-shaped controls, using the
// separately audited pilot container. No generated-source input or grant.
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {randomBytes} from 'node:crypto';
import {shellDocker,writePrivateEvidence,validateDockerContext} from './windows_docker_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {fixedCandidateBundle,fixedCandidateFixture,parseFixedCandidateStream} from './windows_candidate_fixture_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {reservePilotAttempt,readPilotAttempt,recoverPilotAttempt} from './static_web_pilot_attempt_journal.mjs';

const fail=code=>{throw Error(code);};
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const label='codingassistant.static-web-pilot-qualification';
const reportBase=()=>({schemaVersion:1,stage:'AET-8-pilot-durable-lifecycle',status:'BLOCKED',
  recordedAtUtc:new Date().toISOString(),attemptId:null,policySha256:null,engineId:null,
  configurationAudited:false,observations:null,containerRemoved:false,cleanupDebt:false,
  generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false,reason:null});
const errorCode=e=>/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_LIFECYCLE_BLOCKED';
export function pilotHostTools(docker){
  const query=(args,timeout=10000)=>{
    const r=docker(args,timeout);
    if(r?.status!==0||r.timedOut||typeof r.stdout!=='string'||r.stdout.length>65536)fail('PILOT_DOCKER_QUERY_FAILED');
    return r.stdout.trim();
  };
  const json=args=>{try{return JSON.parse(query(args));}catch{fail('PILOT_DOCKER_METADATA_INVALID');}};
  const engine=()=>{
    const name=query(['context','show']);
    const endpoint=json(['context','inspect',name,'--format','{{json .}}'])?.Endpoints?.docker?.Host;
    if(!/^npipe:\/\/\/\/\.\/pipe\/(dockerDesktopLinuxEngine|docker_engine)$/i.test(endpoint??''))
      fail('PILOT_DOCKER_CONTEXT_INVALID');
    const info=json(['info','--format','{{json .}}']);
    validateDockerContext(name,endpoint,info);
    if(typeof info.ID!=='string'||info.ID.length<1||info.ID.length>128||!/^[A-Za-z0-9:_-]+$/.test(info.ID))
      fail('PILOT_ENGINE_ID_UNAVAILABLE');
    return info.ID;
  };
  const list=filter=>query(['ps','-a','--no-trunc','--filter',filter,'--format','{{.ID}}']);
  const inspect=id=>json(['container','inspect',id,'--format','{{json .}}']);
  return {query,engine,list,inspect};
}
export function assertPilotContainerOwned(identity,id,actual){
  if(!digest(id)||actual?.Id!==id||actual.Name!=='/'+identity.name||
    actual.Image!==identity.imageId||actual.Config?.Labels?.[identity.label]!==identity.attemptId||
    actual.Config?.Labels?.['codingassistant.pilot-policy']!==identity.policySha256)
    fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
}
function cleanup(journal,host,{ambiguousCreate=false}={}){
  const identity=journal.identity;
  if(host.engine()!==identity.engineId)fail('PILOT_ENGINE_CHANGED');
  const initial=journal.current;
  const found=host.list('name=^/'+identity.name+'$');
  let id=initial.containerId;
  if(found!==''){
    if(!digest(found)||id!==null&&id!==found)fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
    const actual=host.inspect(found);assertPilotContainerOwned(identity,found,actual);id=found;
  }
  // A create request can outlive a killed client. Do not infer absence is
  // conclusive if the create never returned a durable exact container ID.
  if(found===''&&id===null&&(ambiguousCreate||initial.stage==='Creating'||initial.stage==='CleanupPending'))
    fail('PILOT_CREATE_OUTCOME_UNCERTAIN');
  if(initial.stage==='Cleaned'){
    if(found!==''||id!==null&&host.list('id='+id)!=='')fail('PILOT_CLEANUP_UNCONFIRMED');
    journal.archive();return;
  }
  journal.advance('CleanupPending',id);
  if(found!==''){
    if(host.engine()!==identity.engineId)fail('PILOT_ENGINE_CHANGED');
    assertPilotContainerOwned(identity,id,host.inspect(id));
    host.query(['rm','-f',id],15000);
  }
  if(host.list('name=^/'+identity.name+'$')!==''||
    id!==null&&host.list('id='+id)!=='')fail('PILOT_CLEANUP_UNCONFIRMED');
  journal.advance('Cleaned',id);journal.archive();
}
export function pilotLocalRoot(env){
  if(!env.LOCALAPPDATA||!path.isAbsolute(env.LOCALAPPDATA))fail('PILOT_JOURNAL_PATH_INVALID');
  return path.join(env.LOCALAPPDATA,'CodingAssistant','pilot-attempts');
}
export function qualifyPilotLifecycle({platform=process.platform,env=process.env,docker=shellDocker,
  root,attemptId=randomBytes(16).toString('hex'),control='working'}={}){
  const report=reportBase();
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){report.reason='PILOT_LOCAL_WINDOWS_ONLY';return report;}
  if(!['working','broken','spoof'].includes(control)){report.reason='PILOT_FIXED_CONTROL_REQUIRED';return report;}
  let journal,host,ambiguousCreate=false;
  try{
    root??=pilotLocalRoot(env);host=pilotHostTools(docker);
    if(readPilotAttempt(root))fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    const engineId=host.engine(),image=readLocalImage({platform,env,docker});
    const bundle=fixedCandidateBundle(),policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
    const fixture=fixedCandidateFixture(control,bundle,policy.policySha256,attemptId);
    const identity={...policy.identity(attemptId),engineId,
      candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256};
    Object.assign(report,{attemptId,policySha256:policy.policySha256,engineId});
    journal=reservePilotAttempt(root,identity);
    if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
    if(host.list('label='+label)!==''||host.list('name=^/'+identity.name+'$')!=='')
      fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    journal.advance('Creating');ambiguousCreate=true;
    const id=host.query(policy.createArgs(attemptId),30000);
    if(!digest(id))fail('PILOT_CONTAINER_ID_INVALID');
    journal.advance('Created',id);ambiguousCreate=false;
    policy.audit(attemptId,id,host.inspect(id));journal.advance('Audited');report.configurationAudited=true;
    if(host.engine()!==engineId)fail('PILOT_ENGINE_CHANGED');
    policy.audit(attemptId,id,host.inspect(id));
    journal.advance('Starting');
    // Fixed bundle and one of three frozen controls only. Explicit attach to the
    // already audited ID. No arbitrary input or CLI execution path.
    const result=docker(['start','-a','-i',id],180000,fixture.source);
    const actual=host.inspect(id);assertPilotContainerOwned(identity,id,actual);
    if(actual.State?.Running!==false||actual.State?.Status!=='exited'||actual.State?.ExitCode!==0||
      result?.status!==0||result.timedOut)fail('PILOT_GUEST_EXIT_UNCONFIRMED');
    journal.advance('Exited');
    const parsed=parseFixedCandidateStream(result.stdout??'',fixture.expected);
    report.observations=parsed.observations;
    if(parsed.observations?.status!==(control==='working'?'CHECKS_PASSED_NOT_VERIFIED':'TESTS_FAILED')||
      parsed.observations.completed!==7||parsed.observations.passed!==(control==='working'?7:0))
      fail('PILOT_FIXED_CONTROL_FAILED');
  }catch(e){report.reason=errorCode(e);}
  finally{
    if(journal){
      try{
        // Reserved means no create was requested by this attempt. Never
        // remove a name collision or an unrelated pre-existing container.
        if(journal.current.stage==='Reserved'){
          journal.advance('CleanupPending');journal.advance('Cleaned');journal.archive();
        }else{
          cleanup(journal,host,{ambiguousCreate});report.containerRemoved=true;
        }
      }catch(e){report.reason=errorCode(e);report.cleanupDebt=true;}
    }else if(root){
      try{report.cleanupDebt=readPilotAttempt(root)!==null;}catch{report.cleanupDebt=true;}
    }
  }
  if(report.reason===null&&report.configurationAudited&&report.containerRemoved&&report.observations)
    report.status='PILOT_DURABLE_FIXED_CONTROL_PASS';
  return report;
}
export function reconcilePilotLifecycle({platform=process.platform,env=process.env,docker=shellDocker,root,isAlive}={}){
  const report=reportBase();
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){report.reason='PILOT_LOCAL_WINDOWS_ONLY';return report;}
  try{
    root??=pilotLocalRoot(env);
    const pending=readPilotAttempt(root);
    if(!pending)fail('PILOT_NO_PENDING_ATTEMPT');
    const host=pilotHostTools(docker);
    if(host.engine()!==pending.identity.engineId)fail('PILOT_ENGINE_CHANGED');
    const journal=recoverPilotAttempt(root,{...(isAlive?{isAlive}:{})});
    Object.assign(report,{attemptId:journal.identity.attemptId,policySha256:journal.identity.policySha256,engineId:journal.identity.engineId});
    if(journal.current.stage==='Reserved'){
      journal.advance('CleanupPending');journal.advance('Cleaned');journal.archive();
    }else cleanup(journal,host);
    report.containerRemoved=true;report.status='PILOT_INTERRUPTED_ATTEMPT_RECONCILED';
  }catch(e){report.reason=errorCode(e);report.cleanupDebt=true;}
  return report;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 PILOT_DURABLE_LIFECYCLE: NOT_RUN');return 0;}
  if(argv.length!==1||!['--run','--run-negative-controls','--recover'].includes(argv[0])){
    console.error('Only --run, --run-negative-controls or --recover supported');return 2;
  }
  if(argv[0]==='--run-negative-controls'){
    const negative={schemaVersion:1,stage:'AET-8-pilot-negative-controls',status:'BLOCKED',
      recordedAtUtc:new Date().toISOString(),controls:{},generatedSourceExecuted:false,
      generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false};
    for(const control of ['broken','spoof']){
      const result=qualifyPilotLifecycle({control});negative.controls[control]=result;
      console.log('  '+control+': '+(result.observations?result.observations.passed+'/'+result.observations.required+
        ' ('+result.observations.completed+' completed)':'NOT_COMPLETE')+'; removed: '+result.containerRemoved);
      if(result.status!=='PILOT_DURABLE_FIXED_CONTROL_PASS'){console.log('  blocker: '+result.reason);break;}
    }
    const values=Object.values(negative.controls);
    if(values.length===2&&values.every(v=>v.status==='PILOT_DURABLE_FIXED_CONTROL_PASS'&&v.containerRemoved&&
      v.policySha256===values[0].policySha256&&v.engineId===values[0].engineId))
      negative.status='PILOT_DURABLE_NEGATIVE_CONTROLS_PASS';
    console.log('AET-8 WINDOWS_'+negative.status);
    console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
    console.log('Evidence: '+writePrivateEvidence(negative));
    return negative.status==='PILOT_DURABLE_NEGATIVE_CONTROLS_PASS'?0:2;
  }
  const report=argv[0]==='--run'?qualifyPilotLifecycle():reconcilePilotLifecycle();
  console.log('AET-8 WINDOWS_'+report.status);
  console.log('  configuration audited: '+report.configurationAudited+'; container removed: '+report.containerRemoved+'; cleanup debt: '+report.cleanupDebt);
  if(report.observations)console.log('  working: '+report.observations.passed+'/'+report.observations.required+' ('+report.observations.completed+' completed)');
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(report));
  return ['PILOT_DURABLE_FIXED_CONTROL_PASS','PILOT_INTERRUPTED_ATTEMPT_RECONCILED'].includes(report.status)?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
