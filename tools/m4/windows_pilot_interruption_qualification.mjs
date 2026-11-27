#!/usr/bin/env node
// Owner-local interruption qualification, only for the fixed reference
// control. This supervisor never accepts a path, command or project input.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
import {shellDocker,writePrivateEvidence} from './windows_docker_qualification.mjs';
import {readPilotAttempt,readArchivedPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {pinnedDispatchDocker} from './windows_pinned_dispatch_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {buildQualifiedCandidateBundle,buildQualificationFixture} from './static_web_build_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {createHash} from 'node:crypto';
import {pilotHostTools,assertPilotContainerOwned,pilotLocalRoot,reconcilePilotLifecycle}
  from './windows_pilot_lifecycle_qualification.mjs';

const fail=code=>{throw Error(code);};
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const safeError=e=>/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_INTERRUPTION_BLOCKED';
const script=fileURLToPath(new URL('./windows_pilot_lifecycle_qualification.mjs',import.meta.url));
const pinnedScript=fileURLToPath(new URL('./windows_pinned_dispatch_qualification.mjs',import.meta.url));
const sha=x=>createHash('sha256').update(x).digest('hex');
const stages=['Reserved','Creating','Created','Audited','Starting','CleanupPending','Cleaned'];
// Record the actual current dispatcher/lifecycle closure, not the
// historical bytes of previously reported control runs. The list is
// a subset of the owner-local .NET pin closure, excluding unrelated
// planning and evidence inspection entrypoints.
const pinPaths=Object.freeze(["src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/broken.html","src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/broken.js","src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/working.html","src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/working.js","tools/m4/calculation_journal_contract.mjs","tools/m4/generated_zip_smoke_guest.mjs","tools/m4/profiles/m47-fixed-chromium-chroot-seccomp.json","tools/m4/profiles/playwright-chromium-seccomp.json","tools/m4/smoke_generated_zip.mjs","tools/m4/static_web_build_qualification.mjs","tools/m4/static_web_candidate_assets.mjs","tools/m4/static_web_candidate_build.mjs","tools/m4/static_web_candidate_driver.mjs","tools/m4/static_web_candidate_lifecycle.mjs","tools/m4/static_web_candidate_observations.mjs","tools/m4/static_web_candidate_server.mjs","tools/m4/static_web_evaluation_admission.mjs","tools/m4/static_web_evaluation_candidate.mjs","tools/m4/static_web_evaluation_host_bridge.mjs","tools/m4/static_web_pilot_attempt_journal.mjs","tools/m4/static_web_pilot_browser_boundary.mjs","tools/m4/static_web_pilot_policy.mjs","tools/m4/windows_aet8_pipe_driver.mjs","tools/m4/windows_aet8_trusted_guest.mjs","tools/m4/windows_aet8_trusted_qualification.mjs","tools/m4/windows_calculator_evaluator_guest.mjs","tools/m4/windows_calculator_evaluator_qualification.mjs","tools/m4/windows_calculator_harness.js","tools/m4/windows_candidate_fixture_guest.mjs","tools/m4/windows_candidate_fixture_qualification.mjs","tools/m4/windows_chromium_sandbox_guest.mjs","tools/m4/windows_chromium_sandbox_qualification.mjs","tools/m4/windows_docker_guest.mjs","tools/m4/windows_docker_qualification.mjs","tools/m4/windows_pilot_browser_boundary_guest.mjs","tools/m4/windows_pilot_interruption_qualification.mjs","tools/m4/windows_pilot_lifecycle_qualification.mjs","tools/m4/windows_pinned_dispatch_qualification.mjs"]);
export function dispatchClosure(){
  const root=fileURLToPath(new URL('../../',import.meta.url));
  const pins={};
  for(const relative of pinPaths){
    const file=path.join(root,relative);
    const st=fs.lstatSync(file);
    if(!st.isFile()||st.isSymbolicLink()||st.size>1024*1024||
      fs.realpathSync(file).toLowerCase()!==path.resolve(file).toLowerCase())
      fail('PILOT_DISPATCH_MODULE_INVALID');
    pins[relative]=sha(fs.readFileSync(file));
  }
  return Object.freeze({pins:Object.freeze(pins),sha256:sha(JSON.stringify(pins))});
}
export function requirePinnedInterruptionIdentity(item,policy,bundle,engineId){
  if(!item||item.identity.engineId!==engineId||item.identity.policySha256!==policy.policySha256)
    fail('PILOT_INTERRUPTION_POLICY_CHANGED');
  const fixture=buildQualificationFixture('build-working',bundle,policy.policySha256,item.identity.attemptId);
  const expected={...policy.identity(item.identity.attemptId),engineId,
    candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256};
  if(JSON.stringify(item.identity)!==JSON.stringify(expected))
    fail('PILOT_INTERRUPTION_POLICY_CHANGED');
}
export function bindPinnedInterruptedArchive(root,attemptId,mode,policy,bundle,engineId){
  if(!['cancel','crash'].includes(mode))fail('PILOT_INTERRUPTION_MODE_INVALID');
  const archive=readArchivedPilotAttempt(root,attemptId);
  requirePinnedInterruptionIdentity(archive,policy,bundle,engineId);
  if(JSON.stringify(archive.events.map(x=>x.stage))!==JSON.stringify(stages)||
    archive.recoveryClaimed!==(mode==='crash'))fail('PILOT_INTERRUPTION_ARCHIVE_INVALID');
  return sha(JSON.stringify(archive));
}
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
async function supervise(mode,{root,host,docker,env,pinned=null}){
  const report={mode,attemptId:null,observedRunning:false,interruptionRequested:false,
    childExitConfirmed:false,containerRemoved:false,cleanupDebt:false,reason:null};
  let child,item,exit,spawnError=false,initialOwner=false;
  try{
    if(readPilotAttempt(root)||host.list('label=codingassistant.static-web-pilot-qualification')!=='')
      fail('PILOT_PRIOR_CLEANUP_REQUIRED');
    child=spawn(process.execPath,pinned?[pinnedScript,'--run-fixed-control']:[script,'--run'],
      {env,stdio:'ignore',windowsHide:true,shell:false});
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
    if(pinned)requirePinnedInterruptionIdentity(item,pinned.policy,pinned.bundle,pinned.engineId);
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
    confirmAbsent(item,host,root);
    if(pinned)report.archiveSha256=bindPinnedInterruptedArchive(root,item.identity.attemptId,mode,pinned.policy,pinned.bundle,pinned.engineId);
    report.containerRemoved=true;
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
export async function qualifyPinnedInterruptions({env=process.env}={}){
  const result={schemaVersion:1,stage:'AET-8-pinned-dispatch-interruption-controls',status:'BLOCKED',
    recordedAtUtc:new Date().toISOString(),engineId:null,policySha256:null,adapterSha256:null,
    nodeSha256:env.CA_PILOT_NODE_SHA256??null,dockerSha256:env.CA_PILOT_DOCKER_SHA256??null,
    moduleClosureSha256:null,controls:{},generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,
    runtimeQualified:false,generatedArtifactVerified:false,reason:null};
  if(process.platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){result.reason='PILOT_LOCAL_WINDOWS_ONLY';return result;}
  try{
    // Refuse injected Node preload/loader options in the diagnostic
    // child. Current binary pins are rechecked on every Docker command.
    if(Object.keys(env).some(k=>/^NODE_|^OPENSSL_(CONF|MODULES)$/i.test(k)&&env[k]))
      fail('PILOT_DISPATCH_ENVIRONMENT_OVERRIDE');
    const docker=pinnedDispatchDocker(env),host=pilotHostTools(docker),root=pilotLocalRoot(env);
    const closure=dispatchClosure(),engineId=host.engine(),image=readLocalImage({env,docker});
    const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
    Object.assign(result,{engineId,policySha256:policy.policySha256,adapterSha256:bundle.adapterSha256,moduleClosureSha256:closure.sha256});
    for(const mode of ['cancel','crash']){
      if(dispatchClosure().sha256!==closure.sha256||host.engine()!==engineId)fail('PILOT_DISPATCH_HOST_CHANGED');
      const report=await supervise(mode,{root,host,docker,env,pinned:{bundle,policy,engineId}});
      result.controls[mode]=report;
      if(report.reason||!report.observedRunning||!report.interruptionRequested||
        !report.childExitConfirmed||!report.containerRemoved||report.cleanupDebt||
        !/^[a-f0-9]{64}$/.test(report.archiveSha256??''))
        fail(report.reason??'PILOT_INTERRUPTION_CHECKS_INCOMPLETE');
    }
    if(result.controls.cancel.attemptId===result.controls.crash.attemptId||
      dispatchClosure().sha256!==closure.sha256||host.engine()!==engineId||
      readPilotAttempt(root)!==null||host.list('label=codingassistant.static-web-pilot-qualification')!=='')
      fail('PILOT_DISPATCH_HOST_CHANGED');
    result.status='PILOT_PINNED_INTERRUPTION_CONTROLS_PASS';
  }catch(e){result.reason=safeError(e);}
  return result;
}
export async function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 PILOT_REAL_INTERRUPTION_CONTROLS: NOT_RUN');return 0;}
  if(argv.length!==1||!['--run','--run-pinned'].includes(argv[0])){
    console.error('Only --run or --run-pinned for fixed-source cancellation and crash controls supported');return 2;
  }
  const report=argv[0]==='--run-pinned'?await qualifyPinnedInterruptions():await qualifyPilotInterruptions();
  for(const [name,c] of Object.entries(report.controls))
    console.log('  '+name+': running observed: '+c.observedRunning+'; interrupted: '+c.interruptionRequested+
      '; owner exit confirmed: '+c.childExitConfirmed+'; removed: '+c.containerRemoved+'; cleanup debt: '+c.cleanupDebt);
  console.log('AET-8 WINDOWS_'+report.status);
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(report));
  return ['PILOT_REAL_INTERRUPTION_CONTROLS_PASS','PILOT_PINNED_INTERRUPTION_CONTROLS_PASS'].includes(report.status)?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=await main();
