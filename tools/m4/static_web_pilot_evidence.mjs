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
export function inspectPilotEvidence(names,{platform=process.platform,env=process.env,docker=shellDocker,dispatchPins}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('PILOT_LOCAL_WINDOWS_ONLY');
  if(!Array.isArray(names)||![3,4,5,7].includes(names.length)||new Set(names).size!==names.length)fail('PILOT_RECEIPT_SELECTION_INVALID');
  const [working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions]=names.map(name=>readLocalReceipt(name,env));
  const root=pilotLocalRoot(env),host=pilotHostTools(docker);
  const engineId=host.engine(),imageMetadata=readLocalImage({platform,env,docker});
  if(host.list('label=codingassistant.static-web-pilot-qualification')!=='')fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const input={working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions,imageMetadata,engineId,root};
  const bound=names.length===7?bindPinnedDispatchEvidence(input,dispatchPins??{}):names.length===5?bindBuildQualifiedPilotEvidence(input):names.length===4?bindCompletePilotEvidence(input):bindPilotEvidence(input);
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
        !Array.isArray(input.receiptNames)||![4,5,7].includes(input.receiptNames.length))fail('PILOT_RECEIPT_SELECTION_INVALID');
      names=input.receiptNames;options={docker:pinnedMetadataDocker(),
        ...(names.length===7?{dispatchPins:readDispatchPins()}:{})};
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
