// Cleanup-only exact-attempt adapter. No source, create/start, or execution grant.
// This module is deliberately outside the historical qualification pin closure.
import {createHash} from 'node:crypto';
import {readPilotAttempt,readArchivedPilotAttempt,recoverPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {pilotHostTools,assertPilotContainerOwned,pilotLocalRoot} from './windows_pilot_lifecycle_qualification.mjs';
import {pinnedDispatchDocker} from './windows_pinned_dispatch_qualification.mjs';
const fail=code=>{throw Error(code);};
const label='codingassistant.static-web-pilot-qualification';
const hex=(v,n)=>typeof v==='string'&&v.length===n&&/^[a-f0-9]+$/.test(v);
export function recoveryIdentity(value){
  const keys=['attemptId','engineId','policySha256','imageId','candidateSha256','archiveSha256'];
  if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).length!==keys.length||
    !keys.every(k=>Object.hasOwn(value,k))||!hex(value.attemptId,32)||
    typeof value.engineId!=='string'||!/^[A-Za-z0-9:_-]{1,128}$/.test(value.engineId)||
    !['policySha256','candidateSha256','archiveSha256'].every(k=>hex(value[k],64))||
    typeof value.imageId!=='string'||!value.imageId.startsWith('sha256:')||!hex(value.imageId.slice(7),64))
    fail('PILOT_NATIVE_CLEANUP_IDENTITY_INVALID');
  return Object.freeze({...value,name:'ca-win-pilot-'+value.attemptId,label});
}
function match(expected,actual){
  if(!actual||Object.keys(expected).some(k=>expected[k]!==actual[k]))
    fail('PILOT_NATIVE_CLEANUP_IDENTITY_MISMATCH');
}
// A missing journal or an ambiguous create is not evidence of cleanup.
// Only a complete archived attempt with a durable container ID may pass.
export function inspectExactCleanup(value,{root,host}){
  const expected=recoveryIdentity(value);
  if(host.engine()!==expected.engineId)fail('PILOT_ENGINE_CHANGED');
  if(readPilotAttempt(root)!==null)fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const archive=readArchivedPilotAttempt(root,expected.attemptId);
  match(expected,archive.identity);
  const containerId=archive.events.at(-1).containerId;
  if(!hex(containerId,64)||host.list('name=^/'+expected.name+'$')!==''||
    host.list('id='+containerId)!==''||host.list('label='+label)!==''||
    host.engine()!==expected.engineId||readPilotAttempt(root)!==null)
    fail('PILOT_CLEANUP_UNCONFIRMED');
  return Object.freeze({schemaVersion:1,status:'PILOT_EXACT_CLEANUP_OBSERVED',
    attemptId:expected.attemptId,engineId:expected.engineId,policySha256:expected.policySha256,
    containerId,archiveSha256:createHash('sha256').update(JSON.stringify(archive)).digest('hex'),
    containerRemoved:true,executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
}
// Host must separately establish exclusive recovery ownership and native
// job absence before invoking this module. No CLI or API is exposed yet.
export function recoverExactAttempt(value){
  recoveryIdentity(value);
  const env=process.env,docker=pinnedDispatchDocker(env),root=pilotLocalRoot(env),host=pilotHostTools(docker);
  return recoverExactAttemptWithHost(value,{root,host});
}
// Internal deterministic test seam; the production entrypoint above still
// resolves its own pinned host. Never expose these dependencies over IPC.
export function recoverExactAttemptWithHost(value,{root,host,recover=recoverPilotAttempt}){
  const expected=recoveryIdentity(value);
  if(host.engine()!==expected.engineId)fail('PILOT_ENGINE_CHANGED');
  const pending=readPilotAttempt(root);
  if(pending){
    match(expected,pending.identity);
    const id=pending.current.containerId;
    if(!hex(id,64))fail('PILOT_CREATE_OUTCOME_UNCERTAIN');
    const found=host.list('name=^/'+expected.name+'$');
    if(found!==''&&found!==id)fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
    if(found)assertPilotContainerOwned(expected,id,host.inspect(id));
    // Recover the journal under its exclusive recovery lock before
    // making any mutating Docker request. Recheck the complete identity.
    const journal=recover(root);
    match(expected,journal.identity);
    if(journal.current.containerId!==id)fail('PILOT_NATIVE_CLEANUP_IDENTITY_MISMATCH');
    if(host.engine()!==expected.engineId)fail('PILOT_ENGINE_CHANGED');
    const current=host.list('name=^/'+expected.name+'$');
    if(current!==''&&current!==id)fail('PILOT_CONTAINER_OWNERSHIP_MISMATCH');
    if(journal.current.stage==='Cleaned'){
      if(current!==''||host.list('id='+id)!=='')fail('PILOT_CLEANUP_UNCONFIRMED');
    }else{
      if(current)assertPilotContainerOwned(expected,id,host.inspect(id));
      journal.advance('CleanupPending',id);
      if(current){
        if(host.engine()!==expected.engineId)fail('PILOT_ENGINE_CHANGED');
        assertPilotContainerOwned(expected,id,host.inspect(id));
        host.query(['rm','-f',id],15000);
      }
      if(host.list('name=^/'+expected.name+'$')!==''||host.list('id='+id)!==''||
        host.engine()!==expected.engineId)fail('PILOT_CLEANUP_UNCONFIRMED');
      journal.advance('Cleaned',id);
    }
    journal.archive();
  }
  return inspectExactCleanup(value,{root,host});
}
