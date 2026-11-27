#!/usr/bin/env node
// Historical evidence binding only. No container creation, execution,
// cleanup, approval, registry promotion, or assertion of host attestation.
import path from 'node:path';
import fs from 'node:fs';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {readLocalReceipt,readLocalImage,pinnedMetadataDocker} from './static_web_evaluation_host_bridge.mjs';
import {fixedCandidateBundle,fixedCandidateFixture} from './windows_candidate_fixture_qualification.mjs';
import {fixedBrowserBoundaryBundle,fixedBrowserBoundaryFixture,parseBrowserBoundaryReport} from './static_web_pilot_browser_boundary.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {BUILD_CONTROLS,buildQualifiedCandidateBundle,buildQualificationFixture,buildControlMatches} from './static_web_build_qualification.mjs';
import {readArchivedPilotAttempt,readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {parseCandidateObservations} from './static_web_candidate_observations.mjs';
import {pilotHostTools,pilotLocalRoot} from './windows_pilot_lifecycle_qualification.mjs';
import {shellDocker} from './windows_docker_qualification.mjs';
import {dispatchClosure,requirePinnedInterruptionIdentity,bindPinnedInterruptedArchive} from './windows_pilot_interruption_qualification.mjs';
const fail=code=>{throw Error(code);};
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const canonical=x=>Array.isArray(x)?x.map(canonical):x!==null&&typeof x==='object'?
  Object.fromEntries(Object.keys(x).sort().map(k=>[k,canonical(x[k])])):x;
const json=x=>JSON.stringify(canonical(x));
const sha=x=>createHash('sha256').update(json(x)).digest('hex');
const flags=['generatedSourceExecuted','generatedCodeExecutionEnabled','runtimeQualified','generatedArtifactVerified'];
const base=['schemaVersion','stage','status','recordedAtUtc',...flags];
const timestamp=x=>typeof x==='string'&&Number.isFinite(Date.parse(x));
const header=(x,keys,stage,status)=>exact(x,[...base,...keys])&&x.schemaVersion===1&&
  x.stage===stage&&x.status===status&&timestamp(x.recordedAtUtc)&&flags.every(k=>x[k]===false);
const goodStages=['Reserved','Creating','Created','Audited','Starting','Exited','CleanupPending','Cleaned'];
const interruptedStages=goodStages.filter(x=>x!=='Exited');

export function bindPilotEvidence({working,negatives,interruptions,imageMetadata,engineId,root}){
  if(readPilotAttempt(root)!==null)fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const bundle=fixedCandidateBundle();
  const policy=preparePilotPolicy(imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  if(!header(negatives,['controls'],'AET-8-pilot-negative-controls','PILOT_DURABLE_NEGATIVE_CONTROLS_PASS')||
    !exact(negatives.controls,['broken','spoof'])||
    !header(interruptions,['controls','reason'],'AET-8-pilot-real-interruption-controls','PILOT_REAL_INTERRUPTION_CONTROLS_PASS')||
    interruptions.reason!==null||!exact(interruptions.controls,['cancel','crash']))
    fail('PILOT_QUALIFICATION_RECEIPTS_INCOMPLETE');
  const used=new Set(),attempts={};
  function bindArchive(attemptId,fixtureName,interruption=null){
    if(used.has(attemptId))fail('PILOT_QUALIFICATION_ATTEMPT_REUSED');
    const archive=readArchivedPilotAttempt(root,attemptId);
    const fixture=fixedCandidateFixture(fixtureName,bundle,policy.policySha256,attemptId);
    const expected={...policy.identity(attemptId),engineId,
      candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256};
    if(json(archive.identity)!==json(expected)||
      json(archive.events.map(e=>e.stage))!==json(interruption?interruptedStages:goodStages)||
      archive.recoveryClaimed!==(interruption==='crash'))
      fail('PILOT_QUALIFICATION_ARCHIVE_BINDING_CHANGED');
    used.add(attemptId);
    return {archiveSha256:sha(archive),attemptId,expected:fixture.expected};
  }
  for(const [name,report] of [['working',working],['broken',negatives.controls.broken],['spoof',negatives.controls.spoof]]){
    if(!header(report,['attemptId','policySha256','engineId','configurationAudited','observations','containerRemoved','cleanupDebt','reason'],
      'AET-8-pilot-durable-lifecycle','PILOT_DURABLE_FIXED_CONTROL_PASS')||
      report.policySha256!==policy.policySha256||report.engineId!==engineId||
      report.configurationAudited!==true||report.containerRemoved!==true||report.cleanupDebt!==false||report.reason!==null)
      fail('PILOT_QUALIFICATION_CONTROL_INVALID');
    const bound=bindArchive(report.attemptId,name);
    const o=report.observations;
    if(!o)fail('PILOT_QUALIFICATION_CONTROL_INVALID');
    // Recompute counts/classification from the exact Boolean observation
    // keys and compare the entire stored result. A pass string is not enough.
    const recomputed=parseCandidateObservations(JSON.stringify({schemaVersion:1,...bound.expected,
      security:o.security,build:o.build,infrastructure:o.infrastructure,cases:o.cases}),bound.expected);
    if(json(o)!==json(recomputed)||recomputed.completed!==7||recomputed.passed!==(name==='working'?7:0)||
      recomputed.status!==(name==='working'?'CHECKS_PASSED_NOT_VERIFIED':'TESTS_FAILED'))
      fail('PILOT_QUALIFICATION_CONTROL_INVALID');
    attempts[name]={attemptId:bound.attemptId,archiveSha256:bound.archiveSha256,receiptSha256:sha(report)};
  }
  for(const mode of ['cancel','crash']){
    const report=interruptions.controls[mode];
    if(!exact(report,['mode','attemptId','observedRunning','interruptionRequested','childExitConfirmed','containerRemoved','cleanupDebt','reason'])||
      report.mode!==mode||report.reason!==null||report.cleanupDebt!==false||
      !['observedRunning','interruptionRequested','childExitConfirmed','containerRemoved'].every(k=>report[k]===true))
      fail('PILOT_INTERRUPTION_RECEIPT_INVALID');
    const bound=bindArchive(report.attemptId,'working',mode);
    attempts[mode]={attemptId:bound.attemptId,archiveSha256:bound.archiveSha256,receiptSha256:sha(report)};
  }
  return Object.freeze({schemaVersion:1,status:'PILOT_HISTORICAL_EVIDENCE_BOUND',
    engineId,policySha256:policy.policySha256,policy:policy.descriptor,attempts,
    evidenceSha256:sha({working,negatives,interruptions,attempts,engineId,policySha256:policy.policySha256}),
    hostProvenanceEstablished:false,runtimeQualified:false,executionEnabled:false,generatedArtifactVerified:false});
}

export function bindCompletePilotEvidence(input){
  const prior=bindPilotEvidence(input);
  const {boundary,imageMetadata,engineId,root}=input;
  const bundle=fixedBrowserBoundaryBundle();
  const policy=preparePilotPolicy(imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  if(!header(boundary,['attemptId','policySha256','engineId','configurationAudited','observations','containerRemoved','cleanupDebt','reason'],
    'AET-8-pilot-durable-lifecycle','PILOT_BROWSER_BOUNDARY_PASS')||boundary.policySha256!==policy.policySha256||
    boundary.engineId!==engineId||boundary.configurationAudited!==true||boundary.containerRemoved!==true||
    boundary.cleanupDebt!==false||boundary.reason!==null||Object.values(prior.attempts).some(x=>x.attemptId===boundary.attemptId))
    fail('PILOT_BOUNDARY_RECEIPT_INVALID');
  const archive=readArchivedPilotAttempt(root,boundary.attemptId);
  const fixture=fixedBrowserBoundaryFixture(bundle,policy.policySha256,boundary.attemptId);
  const expected={...policy.identity(boundary.attemptId),engineId,candidateSha256:fixture.expected.candidateSha256,
    archiveSha256:fixture.expected.archiveSha256};
  if(json(archive.identity)!==json(expected)||json(archive.events.map(e=>e.stage))!==json(goodStages)||archive.recoveryClaimed)
    fail('PILOT_BOUNDARY_ARCHIVE_CHANGED');
  const observations=parseBrowserBoundaryReport(JSON.stringify({schemaVersion:1,...fixture.expected,
    checks:boundary.observations?.checks}),fixture.expected);
  if(observations.status!=='FIXED_BROWSER_BOUNDARY_PASS'||json(observations)!==json(boundary.observations))
    fail('PILOT_BOUNDARY_RECEIPT_INVALID');
  const boundaryBinding=Object.freeze({attemptId:boundary.attemptId,policySha256:policy.policySha256,
    adapterSha256:bundle.adapterSha256,archiveSha256:sha(archive),receiptSha256:sha(boundary)});
  return Object.freeze({...prior,status:'PILOT_COMPLETE_HISTORICAL_EVIDENCE_BOUND',
    boundary:boundaryBinding,completeEvidenceSha256:sha({priorEvidenceSha256:prior.evidenceSha256,boundary:boundaryBinding})});
}

// Bind the five new fixed build controls in addition to the original
// six attempts. The old evidence digests and policy are retained verbatim.
export function bindBuildQualifiedPilotEvidence(input){
  const prior=bindCompletePilotEvidence(input);
  const {build,imageMetadata,engineId,root}=input;
  const bundle=buildQualifiedCandidateBundle();
  const policy=preparePilotPolicy(imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  if(!header(build,['controls'],'AET-8-pilot-build-controls','PILOT_BUILD_CONTROLS_PASS')||
    !exact(build.controls,BUILD_CONTROLS)||policy.policySha256===prior.policySha256)
    fail('PILOT_BUILD_EVIDENCE_INVALID');
  // The only permitted policy difference is the new adapter identity.
  if(json({...policy.descriptor,adapterSha256:prior.policy.adapterSha256})!==json(prior.policy))
    fail('PILOT_BUILD_POLICY_CHANGED');
  const used=new Set([...Object.values(prior.attempts).map(x=>x.attemptId),prior.boundary.attemptId]);
  const attempts={};
  for(const name of BUILD_CONTROLS){
    const report=build.controls[name];
    if(!header(report,['attemptId','policySha256','engineId','configurationAudited','observations','containerRemoved','cleanupDebt','reason'],
      'AET-8-pilot-durable-lifecycle','PILOT_BUILD_CONTROL_PASS')||
      report.policySha256!==policy.policySha256||report.engineId!==engineId||
      report.configurationAudited!==true||report.containerRemoved!==true||report.cleanupDebt!==false||
      report.reason!==null||used.has(report.attemptId))
      fail('PILOT_BUILD_CONTROL_INVALID');
    const archive=readArchivedPilotAttempt(root,report.attemptId);
    const fixture=buildQualificationFixture(name,bundle,policy.policySha256,report.attemptId);
    const expected={...policy.identity(report.attemptId),engineId,
      candidateSha256:fixture.expected.candidateSha256,archiveSha256:fixture.expected.archiveSha256};
    if(json(archive.identity)!==json(expected)||json(archive.events.map(e=>e.stage))!==json(goodStages)||archive.recoveryClaimed)
      fail('PILOT_BUILD_ARCHIVE_CHANGED');
    const o=report.observations;
    if(!o)fail('PILOT_BUILD_CONTROL_INVALID');
    const observed=parseCandidateObservations(JSON.stringify({schemaVersion:1,...fixture.expected,
      security:o.security,build:o.build,infrastructure:o.infrastructure,cases:o.cases}),fixture.expected);
    if(json(observed)!==json(o)||!buildControlMatches(name,observed))
      fail('PILOT_BUILD_CONTROL_INVALID');
    used.add(report.attemptId);
    attempts[name]={attemptId:report.attemptId,archiveSha256:sha(archive),receiptSha256:sha(report)};
  }
  const buildQualification=Object.freeze({policySha256:policy.policySha256,policy:policy.descriptor,
    buildSha256:bundle.buildSha256,attempts,receiptSha256:sha(build)});
  return Object.freeze({...prior,status:'PILOT_BUILD_QUALIFIED_HISTORICAL_EVIDENCE_BOUND',buildQualification,
    qualifiedEvidenceSha256:sha({priorEvidenceSha256:prior.completeEvidenceSha256,buildQualification})});
}

// Diagnostic binding of the positive dispatcher run and the two pinned
// interruption controls. Never equivalent to a production trust grant.
export function bindPinnedDispatchEvidence(input,{nodeSha256,dockerSha256}){
  const prior=bindBuildQualifiedPilotEvidence(input);
  const {dispatch,pinnedInterruptions,imageMetadata,engineId,root}=input;
  const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  const digest=x=>typeof x==='string'&&/^[a-f0-9]{64}$/.test(x)&&x.length===64;
  const rawSha=x=>createHash('sha256').update(x).digest('hex');
  const closure=dispatchClosure();
  if(!digest(nodeSha256)||!digest(dockerSha256)||
    !header(dispatch,['attemptId','engineId','policySha256','adapterSha256','transportSha256','nodeSha256','dockerSha256',
      'observations','archiveSha256','configurationAudited','containerRemoved','replayBlocked','cleanupDebt','reason'],
      'AET-8-pinned-dispatch-control','PILOT_PINNED_DISPATCH_PASS')||
    !header(pinnedInterruptions,['engineId','policySha256','adapterSha256','nodeSha256','dockerSha256','moduleClosureSha256',
      'controls','reason'],'AET-8-pinned-dispatch-interruption-controls','PILOT_PINNED_INTERRUPTION_CONTROLS_PASS')||
    !exact(pinnedInterruptions.controls,['cancel','crash'])||pinnedInterruptions.moduleClosureSha256!==closure.sha256)
    fail('PILOT_DISPATCH_EVIDENCE_INVALID');
  for(const report of [dispatch,pinnedInterruptions])
    if(report.engineId!==engineId||report.policySha256!==policy.policySha256||report.adapterSha256!==bundle.adapterSha256||
      report.nodeSha256!==nodeSha256||report.dockerSha256!==dockerSha256||report.reason!==null)
      fail('PILOT_DISPATCH_BINDING_CHANGED');
  const used=new Set([...Object.values(prior.attempts),prior.boundary,...Object.values(prior.buildQualification.attempts)].map(x=>x.attemptId));
  const reserve=id=>{if(used.has(id))fail('PILOT_QUALIFICATION_ATTEMPT_REUSED');used.add(id);};
  reserve(dispatch.attemptId);
  if(dispatch.configurationAudited!==true||dispatch.containerRemoved!==true||dispatch.replayBlocked!==true||dispatch.cleanupDebt!==false)
    fail('PILOT_DISPATCH_EVIDENCE_INVALID');
  const archive=readArchivedPilotAttempt(root,dispatch.attemptId);
  requirePinnedInterruptionIdentity(archive,policy,bundle,engineId);
  const fixture=buildQualificationFixture('build-working',bundle,policy.policySha256,dispatch.attemptId);
  if(json(archive.events.map(e=>e.stage))!==json(goodStages)||archive.recoveryClaimed||
    dispatch.archiveSha256!==rawSha(JSON.stringify(archive))||dispatch.transportSha256!==rawSha(fixture.source))
    fail('PILOT_DISPATCH_ARCHIVE_CHANGED');
  const o=dispatch.observations;
  if(!o)fail('PILOT_DISPATCH_EVIDENCE_INVALID');
  const observed=parseCandidateObservations(JSON.stringify({schemaVersion:1,...fixture.expected,
    security:o.security,build:o.build,infrastructure:o.infrastructure,cases:o.cases}),fixture.expected);
  if(json(observed)!==json(o)||!buildControlMatches('build-working',observed))
    fail('PILOT_DISPATCH_EVIDENCE_INVALID');
  const attempts={working:{attemptId:dispatch.attemptId,archiveSha256:dispatch.archiveSha256,receiptSha256:sha(dispatch)}};
  for(const mode of ['cancel','crash']){
    const report=pinnedInterruptions.controls[mode];
    if(!exact(report,['mode','attemptId','observedRunning','interruptionRequested','childExitConfirmed','containerRemoved',
      'cleanupDebt','reason','archiveSha256'])||report.mode!==mode||report.reason!==null||report.cleanupDebt!==false||
      !['observedRunning','interruptionRequested','childExitConfirmed','containerRemoved'].every(k=>report[k]===true))
      fail('PILOT_PINNED_INTERRUPTION_EVIDENCE_INVALID');
    reserve(report.attemptId);
    if(report.archiveSha256!==bindPinnedInterruptedArchive(root,report.attemptId,mode,policy,bundle,engineId))
      fail('PILOT_DISPATCH_ARCHIVE_CHANGED');
    attempts[mode]={attemptId:report.attemptId,archiveSha256:report.archiveSha256,receiptSha256:sha(report)};
  }
  const dispatchQualification={policySha256:policy.policySha256,adapterSha256:bundle.adapterSha256,
    nodeSha256,dockerSha256,moduleClosureSha256:closure.sha256,attempts,interruptionReceiptSha256:sha(pinnedInterruptions)};
  return Object.freeze({...prior,status:'PILOT_PINNED_DISPATCH_EVIDENCE_BOUND',dispatchQualification,
    dispatchEvidenceSha256:sha({qualifiedEvidenceSha256:prior.qualifiedEvidenceSha256,dispatchQualification})});
}

// Use only the explicit pins passed by the trusted .NET subprocess
// environment. The metadata adapter validates the Docker executable.
export function readDispatchPins(env=process.env){
  const nodeSha256=createHash('sha256').update(fs.readFileSync(process.execPath)).digest('hex');
  if(nodeSha256!==env.CA_PILOT_NODE_SHA256||! /^[a-f0-9]{64}$/.test(env.CA_EVALUATION_DOCKER_SHA256??''))
    fail('PILOT_DISPATCH_PIN_CHANGED');
  return {nodeSha256,dockerSha256:env.CA_EVALUATION_DOCKER_SHA256};
}
// The job receipt records a later fixed-only native qualification. Keep
// its historical assembly/inspector hashes separate from the current
// launcher identity; a rebuilt binary does not inherit executable trust.
const jobControls=['build-working','build-spoof','build-missing-asset'];
const jobReceiptName=/^windows-static-web-[a-f0-9]{12}\.json$/;
const rawSha=x=>createHash('sha256').update(x).digest('hex');
const hash64=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
export function currentJobModulePins(){
  const root=fileURLToPath(new URL('../../',import.meta.url));
  const pins={...dispatchClosure().pins};
  for(const relative of ['static_web_candidate_payload.mjs','static_web_candidate_transport.mjs',
    'static_web_candidate_transport_bridge.mjs','static_web_candidate_dispatch.mjs','windows_job_recovery_control.mjs'].map(x=>'tools/m4/'+x)){
    const file=path.join(root,relative),st=fs.lstatSync(file);
    if(!st.isFile()||st.isSymbolicLink()||st.size>1024*1024||
      fs.realpathSync(file).toLowerCase()!==path.resolve(file).toLowerCase())fail('PILOT_JOB_MODULE_INVALID');
    pins[relative]=rawSha(fs.readFileSync(file));
  }
  return pins;
}
function readJobReceiptBytes(name,env){
  if(!jobReceiptName.test(name??'')||!env.LOCALAPPDATA||!path.isAbsolute(env.LOCALAPPDATA))
    fail('PILOT_JOB_RECEIPT_INVALID');
  const root=path.join(env.LOCALAPPDATA,'CodingAssistant','qualifications'),file=path.join(root,name);
  if(fs.realpathSync(root).toLowerCase()!==path.resolve(root).toLowerCase()||
    fs.realpathSync(file).toLowerCase()!==path.resolve(file).toLowerCase()||!fs.lstatSync(file).isFile())
    fail('PILOT_JOB_RECEIPT_INVALID');
  const fd=fs.openSync(file,'r');
  try{
    const st=fs.fstatSync(fd);
    if(!st.isFile()||st.size<2||st.size>65536)fail('PILOT_JOB_RECEIPT_INVALID');
    const bytes=Buffer.alloc(st.size+1),size=fs.readSync(fd,bytes,0,bytes.length,null);
    if(size!==st.size)fail('PILOT_JOB_RECEIPT_INVALID');
    return bytes.subarray(0,size);
  }finally{fs.closeSync(fd);}
}
export function bindJobOwnershipEvidence(input,pins,{job,normalRaw,recoveryRaw}){
  const prior=bindPinnedDispatchEvidence(input,pins);
  const {root,engineId,imageMetadata}=input;
  const bundle=buildQualifiedCandidateBundle(),policy=preparePilotPolicy(imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  if(!header(job,['jobListAtomic','killOnClose','ownerExitConfirmed','observedProcessCount','observedProcessesExited',
    'jobClosedConfirmed','crashControl','runningContainer','normalReceiptName','normalReceiptSha256',
    'recoveryReceiptName','recoveryReceiptSha256','nodeSha256','dockerSha256','ownerAssemblySha256','harnessAssemblySha256',
    'currentModulePins','inspectorSha256','containerRemoved','cleanupDebt'],
    'AET-8-windows-job-ownership','PILOT_JOB_OWNERSHIP_PASS')||
    !['jobListAtomic','killOnClose','ownerExitConfirmed','observedProcessesExited','jobClosedConfirmed','containerRemoved'].every(k=>job[k]===true)||
    job.cleanupDebt!==false||job.crashControl!=='fixed-attach-stdin-held-open'||
    !Number.isSafeInteger(job.observedProcessCount)||job.observedProcessCount<2||job.observedProcessCount>128||
    job.nodeSha256!==pins.nodeSha256||job.dockerSha256!==pins.dockerSha256||
    !['normalReceiptSha256','recoveryReceiptSha256','ownerAssemblySha256','harnessAssemblySha256','inspectorSha256'].every(k=>hash64(job[k]))||
    ![job.normalReceiptName,job.recoveryReceiptName].every(x=>typeof x==='string'&&jobReceiptName.test(x))||
    job.normalReceiptName===job.recoveryReceiptName||json(job.currentModulePins)!==json(currentJobModulePins()))
    fail('PILOT_JOB_EVIDENCE_INVALID');
  if(!Buffer.isBuffer(normalRaw)||!Buffer.isBuffer(recoveryRaw)||normalRaw.length>65536||recoveryRaw.length>65536||
    rawSha(normalRaw)!==job.normalReceiptSha256||rawSha(recoveryRaw)!==job.recoveryReceiptSha256)
    fail('PILOT_JOB_RECEIPT_CHANGED');
  const normal=JSON.parse(normalRaw.toString('utf8')),recovery=JSON.parse(recoveryRaw.toString('utf8'));
  if(!header(normal,['controls','reason'],'AET-8-candidate-dispatch-lifecycle-controls','PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS')||
    normal.reason!==null||!exact(normal.controls,jobControls))
    fail('PILOT_JOB_NORMAL_INVALID');
  const used=new Set([...Object.values(prior.attempts),prior.boundary,...Object.values(prior.buildQualification.attempts),
    ...Object.values(prior.dispatchQualification.attempts)].map(x=>x.attemptId));
  const reserve=id=>{if(used.has(id))fail('PILOT_QUALIFICATION_ATTEMPT_REUSED');used.add(id);};
  const attempts={};
  for(const control of jobControls){
    const r=normal.controls[control];
    if(!exact(r,['schemaVersion','stage','status','attemptId','engineId','policySha256','transportSha256',
      'configurationAudited','attachClosed','containerRemoved','cleanupDebt','archiveSha256','observations','reason',
      'executionEnabled','runtimeQualified','generatedArtifactVerified','control','qualificationPassed'])||
      r.schemaVersion!==1||r.stage!=='AET-8-candidate-dispatch-lifecycle'||r.control!==control||
      r.engineId!==engineId||r.policySha256!==policy.policySha256||r.reason!==null||r.cleanupDebt!==false||
      !['configurationAudited','attachClosed','containerRemoved','qualificationPassed'].every(k=>r[k]===true)||
      !['executionEnabled','runtimeQualified','generatedArtifactVerified'].every(k=>r[k]===false))
      fail('PILOT_JOB_NORMAL_INVALID');
    reserve(r.attemptId);
    const archive=readArchivedPilotAttempt(root,r.attemptId);
    const fixture=buildQualificationFixture(control,bundle,policy.policySha256,r.attemptId);
    const identity={...policy.identity(r.attemptId),engineId,candidateSha256:fixture.expected.candidateSha256,
      archiveSha256:fixture.expected.archiveSha256};
    if(json(archive.identity)!==json(identity)||json(archive.events.map(e=>e.stage))!==json(goodStages)||
      archive.recoveryClaimed||r.archiveSha256!==rawSha(JSON.stringify(archive))||r.transportSha256!==rawSha(fixture.source))
      fail('PILOT_JOB_ARCHIVE_CHANGED');
    const o=r.observations;
    if(!o)fail('PILOT_JOB_NORMAL_INVALID');
    const checked=parseCandidateObservations(JSON.stringify({schemaVersion:1,...fixture.expected,
      security:o.security,build:o.build,infrastructure:o.infrastructure,cases:o.cases}),fixture.expected);
    if(json(checked)!==json(o)||checked.status!==r.status||!buildControlMatches(control,checked))
      fail('PILOT_JOB_NORMAL_INVALID');
    attempts[control]={attemptId:r.attemptId,archiveSha256:r.archiveSha256,receiptSha256:sha(r)};
  }
  const live=job.runningContainer;
  if(!exact(live,['running','attemptId','ownerPid','engineId','policySha256','containerId'])||
    live.running!==true||!Number.isSafeInteger(live.ownerPid)||live.ownerPid<1||live.engineId!==engineId||
    live.policySha256!==policy.policySha256||!hash64(live.containerId)||
    !exact(recovery,['schemaVersion','stage','status','attemptId','archiveSha256','containerRemoved','cleanupDebt',
      'runtimeQualified','executionEnabled','generatedArtifactVerified','reason'])||
    recovery.schemaVersion!==1||recovery.stage!=='AET-8-job-fixed-recovery'||recovery.status!=='PILOT_JOB_FIXED_RECOVERY_PASS'||
    recovery.attemptId!==live.attemptId||recovery.reason!==null||recovery.containerRemoved!==true||
    !['cleanupDebt','runtimeQualified','executionEnabled','generatedArtifactVerified'].every(k=>recovery[k]===false))
    fail('PILOT_JOB_RECOVERY_INVALID');
  reserve(live.attemptId);
  const archive=readArchivedPilotAttempt(root,live.attemptId);
  if(archive.ownerPid!==live.ownerPid||archive.events.at(-1).containerId!==live.containerId||
    recovery.archiveSha256!==bindPinnedInterruptedArchive(root,live.attemptId,'crash',policy,bundle,engineId))
    fail('PILOT_JOB_ARCHIVE_CHANGED');
  attempts.crash={attemptId:live.attemptId,archiveSha256:recovery.archiveSha256,receiptSha256:sha(recovery)};
  const jobQualification={policySha256:policy.policySha256,adapterSha256:bundle.adapterSha256,
    nodeSha256:pins.nodeSha256,dockerSha256:pins.dockerSha256,jobReceiptSha256:sha(job),
    normalReceiptSha256:job.normalReceiptSha256,recoveryReceiptSha256:job.recoveryReceiptSha256,
    modulePinsSha256:sha(job.currentModulePins),historicalInspectorSha256:job.inspectorSha256,
    historicalOwnerAssemblySha256:job.ownerAssemblySha256,historicalHarnessAssemblySha256:job.harnessAssemblySha256,
    attempts};
  return Object.freeze({...prior,status:'PILOT_JOB_OWNERSHIP_EVIDENCE_BOUND',jobQualification,
    jobEvidenceSha256:sha({dispatchEvidenceSha256:prior.dispatchEvidenceSha256,jobQualification})});
}

export function inspectPilotEvidence(names,{platform=process.platform,env=process.env,docker=shellDocker,dispatchPins}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('PILOT_LOCAL_WINDOWS_ONLY');
  if(!Array.isArray(names)||![3,4,5,7,8].includes(names.length)||new Set(names).size!==names.length)fail('PILOT_RECEIPT_SELECTION_INVALID');
  const [working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions,job]=names.map(name=>readLocalReceipt(name,env));
  const root=pilotLocalRoot(env),host=pilotHostTools(docker);
  const engineId=host.engine(),imageMetadata=readLocalImage({platform,env,docker});
  if(host.list('label=codingassistant.static-web-pilot-qualification')!=='')fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const input={working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions,imageMetadata,engineId,root};
  if(job&&new Set([...names,job.normalReceiptName,job.recoveryReceiptName]).size!==names.length+2)
    fail('PILOT_RECEIPT_SELECTION_INVALID');
  const bound=names.length===8?bindJobOwnershipEvidence(input,dispatchPins??{}, {job,
    normalRaw:readJobReceiptBytes(job.normalReceiptName,env),recoveryRaw:readJobReceiptBytes(job.recoveryReceiptName,env)}):
    names.length===7?bindPinnedDispatchEvidence(input,dispatchPins??{}):names.length===5?bindBuildQualifiedPilotEvidence(input):names.length===4?bindCompletePilotEvidence(input):bindPilotEvidence(input);
  if(host.engine()!==engineId||host.list('label=codingassistant.static-web-pilot-qualification')!==''||readPilotAttempt(root)!==null)
    fail('PILOT_HOST_CHANGED');
  return bound;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('PILOT_HISTORICAL_EVIDENCE NOT_RUN');return 0;}
  try{
    let names,options={};
    if(argv.length===1&&argv[0]==='--stdin'){
      const buffer=Buffer.alloc(4097);let size=0;
      while(size<buffer.length){const count=fs.readSync(0,buffer,size,buffer.length-size,null);if(!count)break;size+=count;}
      if(size>4096)fail('PILOT_RECEIPT_SELECTION_INVALID');
      const input=JSON.parse(buffer.subarray(0,size).toString('utf8'));
      if(!exact(input,['operation','receiptNames'])||input.operation!=='inspect-complete-pilot'||
        !Array.isArray(input.receiptNames)||![4,5,7,8].includes(input.receiptNames.length))fail('PILOT_RECEIPT_SELECTION_INVALID');
      names=input.receiptNames;options={docker:pinnedMetadataDocker(),
        ...(names.length>=7?{dispatchPins:readDispatchPins()}:{})};
    }else if([4,5,6].includes(argv.length)&&argv[0]==='--inspect')names=argv.slice(1);
    else if(argv.length===8&&argv[0]==='--inspect-pinned-dispatch'){
      names=argv.slice(1);
      // Read-only mode: use the explicit pinned metadata transport, not the
      // executable wrapper. Hash the exact current Node binary as well.
      const nodeSha256=createHash('sha256').update(fs.readFileSync(process.execPath)).digest('hex');
      if(nodeSha256!==process.env.CA_PILOT_NODE_SHA256)fail('PILOT_DISPATCH_PIN_CHANGED');
      const dockerSha256=process.env.CA_PILOT_DOCKER_SHA256;
      options={docker:pinnedMetadataDocker({...process.env,CA_EVALUATION_DOCKER_EXE:process.env.CA_PILOT_DOCKER_EXE,
        CA_EVALUATION_DOCKER_SHA256:dockerSha256}),dispatchPins:{nodeSha256,dockerSha256}};
    }
    else fail('PILOT_RECEIPT_SELECTION_INVALID');
    console.log(JSON.stringify(inspectPilotEvidence(names,options)));return 0;
  }catch(e){
    console.log(JSON.stringify({status:'BLOCKED',reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_EVIDENCE_BLOCKED',
      hostProvenanceEstablished:false,runtimeQualified:false,executionEnabled:false,generatedArtifactVerified:false}));return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
