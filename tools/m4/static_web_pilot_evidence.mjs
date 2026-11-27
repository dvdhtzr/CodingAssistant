#!/usr/bin/env node
// Historical evidence binding only. No container creation, execution,
// cleanup, approval, registry promotion, or assertion of host attestation.
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {readLocalReceipt,readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {fixedCandidateBundle,fixedCandidateFixture} from './windows_candidate_fixture_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {readArchivedPilotAttempt,readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {parseCandidateObservations} from './static_web_candidate_observations.mjs';
import {pilotHostTools,pilotLocalRoot} from './windows_pilot_lifecycle_qualification.mjs';
import {shellDocker} from './windows_docker_qualification.mjs';
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

export function inspectPilotEvidence(names,{platform=process.platform,env=process.env,docker=shellDocker}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('PILOT_LOCAL_WINDOWS_ONLY');
  if(!Array.isArray(names)||names.length!==3||new Set(names).size!==3)fail('PILOT_RECEIPT_SELECTION_INVALID');
  const [working,negatives,interruptions]=names.map(name=>readLocalReceipt(name,env));
  const root=pilotLocalRoot(env),host=pilotHostTools(docker);
  const engineId=host.engine(),imageMetadata=readLocalImage({platform,env,docker});
  if(host.list('label=codingassistant.static-web-pilot-qualification')!=='')fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const bound=bindPilotEvidence({working,negatives,interruptions,imageMetadata,engineId,root});
  if(host.engine()!==engineId||host.list('label=codingassistant.static-web-pilot-qualification')!==''||readPilotAttempt(root)!==null)
    fail('PILOT_HOST_CHANGED');
  return bound;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('PILOT_HISTORICAL_EVIDENCE NOT_RUN');return 0;}
  try{
    if(argv.length!==4||argv[0]!=='--inspect')fail('PILOT_RECEIPT_SELECTION_INVALID');
    console.log(JSON.stringify(inspectPilotEvidence(argv.slice(1))));return 0;
  }catch(e){
    console.log(JSON.stringify({status:'BLOCKED',reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_EVIDENCE_BLOCKED',
      hostProvenanceEstablished:false,runtimeQualified:false,executionEnabled:false,generatedArtifactVerified:false}));return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
