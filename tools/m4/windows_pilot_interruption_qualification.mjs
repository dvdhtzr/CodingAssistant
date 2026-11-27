#!/usr/bin/env node
// Owner-local interruption qualification, only for the fixed reference
// control. This supervisor never accepts a path, command or project input.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
import {shellDocker,writePrivateEvidence} from './windows_docker_qualification.mjs';
import {readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {pilotHostTools,assertPilotContainerOwned,pilotLocalRoot,reconcilePilotLifecycle}
  from './windows_pilot_lifecycle_qualification.mjs';

const fail=code=>{throw Error(code);};
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const safeError=e=>/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_INTERRUPTION_BLOCKED';
const script=fileURLToPath(new URL('./windows_pilot_lifecycle_qualification.mjs',import.meta.url));
function readArchive(root,id){
  const file=path.join(root,'attempt-'+id,'events.jsonl');
  const stat=fs.lstatSync(file);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>65536)fail('PILOT_ARCHIVE_INVALID');
  const events=fs.readFileSync(file,'utf8').trimEnd().split('\n').map(x=>JSON.parse(x));
  if(events.at(-1)?.stage!=='Cleaned'||!events.some(e=>e.stage==='Starting')||
    events.some(e=>e.stage==='Exited'))fail('PILOT_INTERRUPTION_ARCHIVE_INVALID');
  return events.at(-1).containerId;
}
// Both actions require the exact current child-owned journal and an
// independently observed running container, never just a start request.
export function observeRunningPilot(root,ownerPid,host){
  const item=readPilotAttempt(root);
  if(!item)return null;
  if(item.ownerPid!==ownerPid)fail('PILOT_OWNER_MISMATCH');
  if(item.current.stage!=='Starting')return null;
  const id=item.current.containerId;
  if(host.engine()!==item.identity.engineId)fail('PILOT_ENGINE_CHANGED');
  const actual=host.inspect(id);
  assertPilotContainerOwned(item.identity,id,actual);
  if(actual.State?.Status!=='running'||actual.State?.Running!==true)return null;
  return item;
}
export function cancelObservedPilot(item,host){
  const id=item.current.containerId;
  if(item.current.stage!=='Starting'||host.engine()!==item.identity.engineId)
    fail('PILOT_CANCELLATION_NOT_CURRENT');
  const actual=host.inspect(id);
  assertPilotContainerOwned(item.identity,id,actual);
  if(actual.State?.Status!=='running'||actual.State?.Running!==true)
    fail('PILOT_CANCELLATION_NOT_CURRENT');
  // Docker confirms the signal was sent to a running guest. The existing
  // lifecycle owner handles its failed exit, cleanup and durable archival.
  const result=host.query(['kill','--signal=KILL',id],15000);
  if(result!==id)fail('PILOT_CANCELLATION_UNCONFIRMED');
}
function confirmAbsent(item,host,root){
  if(host.engine()!==item.identity.engineId||
    host.list('name=^/'+item.identity.name+'$')!==''||
    host.list('id='+item.current.containerId)!==''||readPilotAttempt(root)!==null||
    readArchive(root,item.identity.attemptId)!==item.current.containerId)
    fail('PILOT_INTERRUPTION_CLEANUP_UNCONFIRMED');
}
async function supervise(mode,{root,host,docker,env}){
  const report={mode,attemptId:null,observedRunning:false,interruptionRequested:false,
    childExitConfirmed:false,containerRemoved:false,cleanupDebt:false,reason:null};
  let child,item,exit,spawnError=false,initialOwner=false;
  try{
    if(readPilotAttempt(root)||host.list('label=codingassistant.static-web-pilot-qualification')!=='')
      fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    child=spawn(process.execPath,[script,'--run'],{env,stdio:'ignore',windowsHide:true,shell:false});
    child.once('error',()=>{spawnError=true;});
    child.once('exit',(code,signal)=>{exit={code,signal};});
    const deadline=Date.now()+60000;
    while(Date.now()<deadline&&!exit&&!spawnError){
      if(child.pid){
        // A transient not-yet-created journal is normal before reserve.
        let pending;
        try{pending=readPilotAttempt(root);}
        catch(e){
          // The child may be between exclusive mkdir and fsync of the
          // identity/event files. Retry observation only, never mutate.
          if(e?.message!=='PILOT_JOURNAL_INVALID')throw e;
          await sleep(150);continue;
        }
        if(pending&&pending.ownerPid===child.pid)initialOwner=true;
        if(pending&&pending.ownerPid!==child.pid)fail('PILOT_OWNER_MISMATCH');
        if(pending?.current.stage==='Starting'){
          try{item=observeRunningPilot(root,child.pid,host);}
          catch(e){if(e?.message!=='PILOT_JOURNAL_INVALID')throw e;}
          if(item)break;
        }
      }
      await sleep(150);
    }
    if(!item||exit||spawnError)fail('PILOT_RUNNING_CONTROL_NOT_OBSERVED');
    report.attemptId=item.identity.attemptId;report.observedRunning=true;
    if(mode==='cancel')cancelObservedPilot(item,host);
    else if(mode==='crash'){
      // Terminate only the child process created by this supervisor.
      // This is a deliberate abrupt owner interruption, not a guest exit.
      if(!child.kill('SIGKILL'))fail('PILOT_OWNER_INTERRUPTION_UNCONFIRMED');
    }else fail('PILOT_INTERRUPTION_MODE_INVALID');
    report.interruptionRequested=true;
    const exitDeadline=Date.now()+45000;
    while(!exit&&!spawnError&&Date.now()<exitDeadline)await sleep(100);
    if(!exit||exit.code===0||(mode==='cancel'&&exit.code!==2))
      fail('PILOT_INTERRUPTION_EXIT_UNCONFIRMED');
    report.childExitConfirmed=true;
    if(mode==='crash'){
      const pending=readPilotAttempt(root);
      if(!pending||pending.ownerPid!==child.pid||pending.identity.attemptId!==item.identity.attemptId||
        pending.current.stage!=='Starting')fail('PILOT_CRASH_JOURNAL_NOT_RETAINED');
      const recovered=reconcilePilotLifecycle({root,docker,env});
      if(recovered.status!=='PILOT_INTERRUPTED_ATTEMPT_RECONCILED'||!recovered.containerRemoved)
        fail(recovered.reason??'PILOT_RECOVERY_FAILED');
    }
    confirmAbsent(item,host,root);report.containerRemoved=true;
  }catch(e){report.reason=safeError(e);}
  finally{
    // On a diagnostic failure, stop our own still-active child, but do
    // not take over another process's attempt or claim successful cleanup.
    if(child&&!exit&&!spawnError){
      child.kill('SIGKILL');
      const deadline=Date.now()+10000;
      while(!exit&&!spawnError&&Date.now()<deadline)await sleep(100);
    }
    try{
      const pending=readPilotAttempt(root);
      if(pending&&child&&exit&&pending.ownerPid===child.pid&&report.reason!==null){
        // Best-effort cleanup only; an interrupted/ambiguous recovery is
        // deliberately left blocked. Never turn a failed probe into PASS.
        const recovery=reconcilePilotLifecycle({root,docker,env});
        if(recovery.containerRemoved&&item)report.containerRemoved=true;
      }
      report.cleanupDebt=readPilotAttempt(root)!==null||
        host.list('label=codingassistant.static-web-pilot-qualification')!=='';
      if(child&&!exit&&!spawnError||initialOwner&&!report.containerRemoved&&report.reason!==null)
        report.cleanupDebt=true;
    }catch{report.cleanupDebt=true;}
  }
  return report;
}
export async function qualifyPilotInterruptions({platform=process.platform,env=process.env,docker=shellDocker}={}){
  const result={schemaVersion:1,stage:'AET-8-pilot-real-interruption-controls',status:'BLOCKED',
    recordedAtUtc:new Date().toISOString(),controls:{},generatedSourceExecuted:false,
    generatedCodeExecutionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false,reason:null};
  if(platform!=='win32'||process.platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){
    result.reason='PILOT_LOCAL_WINDOWS_ONLY';return result;
  }
  try{
    const root=pilotLocalRoot(env),host=pilotHostTools(docker);
    for(const mode of ['cancel','crash']){
      const report=await supervise(mode,{root,host,docker,env});result.controls[mode]=report;
      if(report.reason||!report.observedRunning||!report.interruptionRequested||
        !report.childExitConfirmed||!report.containerRemoved||report.cleanupDebt)
        fail(report.reason??'PILOT_INTERRUPTION_CHECKS_INCOMPLETE');
    }
    result.status='PILOT_REAL_INTERRUPTION_CONTROLS_PASS';
  }catch(e){result.reason=safeError(e);}
  return result;
}
export async function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 PILOT_REAL_INTERRUPTION_CONTROLS: NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run'){
    console.error('Only --run for fixed-source cancellation and crash controls supported');return 2;
  }
  const report=await qualifyPilotInterruptions();
  for(const [name,c] of Object.entries(report.controls))
    console.log('  '+name+': running observed: '+c.observedRunning+'; interrupted: '+c.interruptionRequested+
      '; owner exit confirmed: '+c.childExitConfirmed+'; removed: '+c.containerRemoved+'; cleanup debt: '+c.cleanupDebt);
  console.log('AET-8 WINDOWS_'+report.status);
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(report));
  return report.status==='PILOT_REAL_INTERRUPTION_CONTROLS_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=await main();
