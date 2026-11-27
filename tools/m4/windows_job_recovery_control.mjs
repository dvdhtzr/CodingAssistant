// Fixed cleanup-only qualification command. No project input, launch,
// replay, arbitrary command or removal of unrelated containers.
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {pinnedDispatchDocker} from './windows_pinned_dispatch_qualification.mjs';
import {pilotHostTools,pilotLocalRoot,reconcilePilotLifecycle,assertPilotContainerOwned} from './windows_pilot_lifecycle_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {buildQualifiedCandidateBundle} from './static_web_build_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {requirePinnedInterruptionIdentity,bindPinnedInterruptedArchive} from './windows_pilot_interruption_qualification.mjs';
import {readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {writePrivateEvidence} from './windows_docker_qualification.mjs';
export function recoverFixedJobControl(){
  const report={schemaVersion:1,stage:'AET-8-job-fixed-recovery',status:'BLOCKED',attemptId:null,
    archiveSha256:null,containerRemoved:false,cleanupDebt:true,runtimeQualified:false,
    executionEnabled:false,generatedArtifactVerified:false,reason:null};
  try{
    const env=process.env,docker=pinnedDispatchDocker(env),host=pilotHostTools(docker),root=pilotLocalRoot(env);
    const pending=readPilotAttempt(root),engineId=host.engine(),image=readLocalImage({env,docker});
    const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
    requirePinnedInterruptionIdentity(pending,policy,bundle,engineId);
    if(pending.current.stage!=='Starting')throw Error('PILOT_JOB_RECOVERY_STAGE_INVALID');
    report.attemptId=pending.identity.attemptId;
    const outcome=reconcilePilotLifecycle({root,docker,env});
    if(outcome.status!=='PILOT_INTERRUPTED_ATTEMPT_RECONCILED'||!outcome.containerRemoved||
      outcome.cleanupDebt||host.engine()!==engineId||readPilotAttempt(root)!==null||
      host.list('label=codingassistant.static-web-pilot-qualification')!=='')
      throw Error('PILOT_JOB_RECOVERY_UNCONFIRMED');
    report.archiveSha256=bindPinnedInterruptedArchive(root,report.attemptId,'crash',policy,bundle,engineId);
    report.containerRemoved=true;report.cleanupDebt=false;report.status='PILOT_JOB_FIXED_RECOVERY_PASS';
  }catch(e){report.reason=/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_JOB_RECOVERY_BLOCKED';}
  return report;
}
// Observe only the exact live fixed control, using the same pinned
// read-only metadata queries. No mutation or signal in this mode.
export function observeFixedJobControl(){
  const env=process.env,docker=pinnedDispatchDocker(env),host=pilotHostTools(docker),root=pilotLocalRoot(env);
  const pending=readPilotAttempt(root);
  if(!pending||pending.current.stage!=='Starting')return {running:false};
  const engineId=host.engine(),image=readLocalImage({env,docker});
  const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
  requirePinnedInterruptionIdentity(pending,policy,bundle,engineId);
  const actual=host.inspect(pending.current.containerId);
  assertPilotContainerOwned(pending.identity,pending.current.containerId,actual);
  if(actual.State?.Status!=='running'||actual.State?.Running!==true)return {running:false};
  const current=readPilotAttempt(root);
  if(host.engine()!==engineId||current?.ownerPid!==pending.ownerPid||
    current.identity.attemptId!==pending.identity.attemptId||current.current.stage!=='Starting'||
    current.current.containerId!==pending.current.containerId)return {running:false};
  return {running:true,attemptId:pending.identity.attemptId,ownerPid:pending.ownerPid,engineId,
    policySha256:policy.policySha256,containerId:pending.current.containerId};
}
function main(args){
  if(!args.length){console.log('PILOT_JOB_FIXED_RECOVERY NOT_RUN');return 0;}
  if(args.length===1&&args[0]==='--observe-fixed'){
    try{console.log(JSON.stringify(observeFixedJobControl()));return 0;}
    catch(e){console.log(JSON.stringify({running:false,reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_JOB_OBSERVATION_BLOCKED'}));return 0;}
  }
  if(args.length!==1||args[0]!=='--recover-fixed')return 2;
  const report=recoverFixedJobControl();
  writePrivateEvidence(report);
  return report.status==='PILOT_JOB_FIXED_RECOVERY_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main(process.argv.slice(2));
