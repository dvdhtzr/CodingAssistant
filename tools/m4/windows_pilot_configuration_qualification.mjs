#!/usr/bin/env node
// Owner-invoked create/inspect/remove probe only. Never starts a guest.
// Uses the candidate-shaped adapter identity, not generated project input.
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {randomBytes} from 'node:crypto';
import {shellDocker,writePrivateEvidence} from './windows_docker_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {fixedCandidateBundle} from './windows_candidate_fixture_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';

const fail=code=>{throw Error(code);};
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
export function qualifyPilotConfiguration({platform=process.platform,env=process.env,
  docker=shellDocker,attemptId=randomBytes(16).toString('hex')}={}){
  const report={schemaVersion:1,stage:'AET-8-pilot-container-configuration',status:'BLOCKED',
    recordedAtUtc:new Date().toISOString(),policy:null,policySha256:null,attemptId:null,
    containerId:null,configurationAudited:false,containerRemoved:false,
    guestStarted:false,generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,
    runtimeQualified:false,generatedArtifactVerified:false,reason:null};
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){
    report.reason='PILOT_LOCAL_WINDOWS_ONLY';return report;
  }
  let attempted=false,identity=null,policy=null;
  const query=(args,timeout=10000)=>{
    const result=docker(args,timeout);
    if(result?.status!==0||result.timedOut||typeof result.stdout!=='string'||result.stdout.length>65536)
      fail('PILOT_DOCKER_QUERY_FAILED');
    return result.stdout.trim();
  };
  const list=filter=>query(['ps','-a','--no-trunc','--filter',filter,'--format','{{.ID}}']);
  const inspect=target=>{
    let result;try{result=JSON.parse(query(['container','inspect',target,'--format','{{json .}}']));}
    catch{fail('PILOT_CONTAINER_INSPECT_FAILED');}
    return result;
  };
  try{
    const image=readLocalImage({platform,env,docker});
    const bundle=fixedCandidateBundle();
    policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
    identity=policy.identity(attemptId);
    Object.assign(report,{attemptId,policy:policy.descriptor,policySha256:policy.policySha256});
    // A crash or ambiguous prior cleanup is not permission to start
    // another qualification container. Do not remove a prior attempt.
    if(list('label='+identity.label)!=='')fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    if(list('name=^/'+identity.name+'$')!=='')fail('PILOT_NAME_COLLISION');
    attempted=true;
    const id=query(policy.createArgs(attemptId),30000);
    if(!digest(id))fail('PILOT_CONTAINER_ID_INVALID');
    report.containerId=id;
    const observed=inspect(id);
    policy.audit(attemptId,id,observed);
    report.configurationAudited=true;
  }catch(e){
    report.reason=/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_CONFIGURATION_BLOCKED';
  }finally{
    if(attempted){
      try{
        // Even after a failed create/inspect, reconcile the unique name.
        // Only remove an exact owned container after checking its immutable
        // ID and both labels. Never remove a mismatched or running guest.
        const found=list('name=^/'+identity.name+'$');
        if(found!==''){
          if(!digest(found)||report.containerId!==null&&report.containerId!==found)
            fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
          const observed=inspect(found);
          if(observed.Id!==found||observed.Name!=='/'+identity.name||
            observed.Image!==identity.imageId||observed.State?.Status!=='created'||
            observed.State?.Running!==false||
            observed.Config?.Labels?.[identity.label]!==attemptId||
            observed.Config?.Labels?.['codingassistant.pilot-policy']!==policy.policySha256)
            fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
          report.containerId=found;
          query(['rm',found],15000);
        }
        report.containerRemoved=list('name=^/'+identity.name+'$')===''&&
          (report.containerId===null||list('id='+report.containerId)==='');
      }catch{report.containerRemoved=false;}
      if(!report.containerRemoved)report.reason='PILOT_CONTAINER_CLEANUP_UNCONFIRMED';
    }
  }
  if(report.reason===null&&report.configurationAudited&&report.containerRemoved)
    report.status='PILOT_CONTAINER_CONFIGURATION_PASS';
  return report;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 PILOT_CONTAINER_CONFIGURATION: NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run'){
    console.error('Only --run for the non-starting configuration probe is supported');return 2;
  }
  const report=qualifyPilotConfiguration();
  console.log('AET-8 WINDOWS_'+report.status);
  console.log('  container configuration audited: '+report.configurationAudited);
  console.log('  container removed: '+report.containerRemoved);
  console.log('  guest started: false; generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('Evidence: '+writePrivateEvidence(report));
  return report.status==='PILOT_CONTAINER_CONFIGURATION_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=main();
