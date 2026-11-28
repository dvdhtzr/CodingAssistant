import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {recoveryIdentity,inspectExactCleanup} from '../tools/m4/native_exact_cleanup.mjs';
import {reservePilotAttempt} from '../tools/m4/static_web_pilot_attempt_journal.mjs';

const value={attemptId:'a'.repeat(32),engineId:'test-engine',policySha256:'b'.repeat(64),
  imageId:'sha256:'+'c'.repeat(64),candidateSha256:'d'.repeat(64),archiveSha256:'e'.repeat(64)};
const id='f'.repeat(64);
const host={engine:()=>value.engineId,list:()=>''};
function fixture(t,archive=true){
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-native-recovery-'));
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  const journal=reservePilotAttempt(root,recoveryIdentity(value));
  journal.advance('Creating');journal.advance('Created',id);
  if(archive){journal.advance('CleanupPending');journal.advance('Cleaned');journal.archive();}
  return root;
}
test('exact archived attempt plus independent absence queries confirms cleanup only',t=>{
  const root=fixture(t),filters=[];
  const result=inspectExactCleanup(value,{root,host:{...host,list:f=>{filters.push(f);return '';}}});
  assert.equal(result.status,'PILOT_EXACT_CLEANUP_OBSERVED');
  assert.equal(result.containerId,id);
  assert.match(result.archiveSha256,/^[a-f0-9]{64}$/);
  assert.equal(result.containerRemoved,true);
  for(const flag of ['executionEnabled','runtimeQualified','generatedArtifactVerified'])assert.equal(result[flag],false);
  assert.deepEqual(filters,['name=^/ca-win-pilot-'+value.attemptId+'$','id='+id,'label=codingassistant.static-web-pilot-qualification']);
});
test('changed identity, engine, remaining container or active journal cannot confirm cleanup',t=>{
  const root=fixture(t);
  for(const key of ['policySha256','candidateSha256','archiveSha256','imageId']){
    const changed={...value,[key]:(key==='imageId'?'sha256:':'')+'0'.repeat(64)};
    assert.throws(()=>inspectExactCleanup(changed,{root,host}),/PILOT_NATIVE_CLEANUP_IDENTITY_MISMATCH/);
  }
  assert.throws(()=>inspectExactCleanup(value,{root,host:{...host,engine:()=> 'other-engine'}}),/PILOT_ENGINE_CHANGED/);
  for(const prefix of ['name=','id=','label='])
    assert.throws(()=>inspectExactCleanup(value,{root,host:{...host,list:f=>f.startsWith(prefix)?id:''}}),/PILOT_CLEANUP_UNCONFIRMED/);
  const pending=fixture(t,false);
  assert.throws(()=>inspectExactCleanup(value,{root:pending,host}),/PILOT_PRIOR_CLEANUP_REQUIRED/);
});
test('invalid identities and missing journal never imply cleanup',t=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-native-recovery-empty-'));
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  assert.throws(()=>recoveryIdentity({...value,command:'rm'}),/PILOT_NATIVE_CLEANUP_IDENTITY_INVALID/);
  assert.throws(()=>recoveryIdentity({...value,attemptId:'../bad'}),/PILOT_NATIVE_CLEANUP_IDENTITY_INVALID/);
  assert.throws(()=>inspectExactCleanup(value,{root,host}));
});
