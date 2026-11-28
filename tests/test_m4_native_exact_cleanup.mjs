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

import {parseRecoveryRequest} from '../tools/m4/native_cleanup_observation_bridge.mjs';
test('observation bridge accepts only bounded exact read-only request',()=>{
  const request={operation:'inspect-exact-cleanup',identity:value};
  assert.deepEqual(parseRecoveryRequest(JSON.stringify(request)),value);
  for(const changed of [{...request,operation:'recover-exact-cleanup'},{...request,command:'rm'},
    {...request,identity:{...value,source:'anything'}}])assert.throws(()=>parseRecoveryRequest(JSON.stringify(changed)));
  assert.throws(()=>parseRecoveryRequest(' '.repeat(4097)));
});

import {recoverExactAttemptWithHost} from '../tools/m4/native_exact_cleanup.mjs';
import {recoverPilotAttempt,readPilotAttempt,readArchivedPilotAttempt} from '../tools/m4/static_web_pilot_attempt_journal.mjs';

const expected=recoveryIdentity(value);
const deadOwner=root=>recoverPilotAttempt(root,{isAlive:()=>false});
function simulatedDocker(){
  const state={present:true,engine:value.engineId,other:true,queries:[],keep:false};
  const actual={Id:id,Name:'/'+expected.name,Image:value.imageId,
    Config:{Labels:{[expected.label]:value.attemptId,'codingassistant.pilot-policy':value.policySha256}}};
  const docker={
    engine:()=>state.engine,
    list:filter=>state.present?id:'',
    inspect:()=>actual,
    query:(args,timeout)=>{
      assert.deepEqual(args,['rm','-f',id]);assert.equal(timeout,15000);
      state.queries.push(args);
      if(!state.keep)state.present=false;
    }
  };
  return {state,actual,docker};
}
function active(t){
  const root=fixture(t,false),sim=simulatedDocker();
  const run=(overrides={})=>recoverExactAttemptWithHost(value,{root,host:sim.docker,recover:deadOwner,...overrides});
  return {...sim,root,run};
}
test('active exact recovery archives the interrupted attempt and does not replay',t=>{
  const x=active(t);
  const result=x.run();
  assert.equal(result.status,'PILOT_EXACT_CLEANUP_OBSERVED');
  assert.equal(result.containerId,id);
  assert.equal(result.executionEnabled,false);
  assert.equal(result.runtimeQualified,false);
  assert.equal(result.generatedArtifactVerified,false);
  assert.equal(readPilotAttempt(x.root),null);
  const archive=readArchivedPilotAttempt(x.root,value.attemptId);
  assert.equal(archive.recoveryClaimed,true);
  assert.deepEqual(archive.events.map(e=>e.stage),['Reserved','Creating','Created','CleanupPending','Cleaned']);
  assert.deepEqual(x.state.queries,[['rm','-f',id]]);
  assert.equal(x.state.other,true);
  assert.deepEqual(x.run(),result);
  assert.equal(x.state.queries.length,1);
});
test('live owner and existing recovery lock reject mutation',t=>{
  const x=active(t);
  assert.throws(()=>x.run({recover:root=>recoverPilotAttempt(root,{isAlive:()=>true})}),/PILOT_OWNER_STILL_ACTIVE/);
  deadOwner(x.root);
  assert.throws(()=>x.run(),/PILOT_RECOVERY_ALREADY_CLAIMED/);
  assert.equal(x.state.queries.length,0);
  assert.equal(readPilotAttempt(x.root).current.stage,'Created');
});
test('changed identity or durable ID after recovery claim cannot mutate',async t=>{
  for(const field of ['policySha256','candidateSha256','archiveSha256','imageId','containerId']){
    await t.test(field,sub=>{
      const x=active(sub);
      assert.throws(()=>x.run({recover:root=>{
        const journal=deadOwner(root);
        return field==='containerId'?{...journal,current:{...journal.current,containerId:'0'.repeat(64)}}:
          {...journal,identity:{...journal.identity,[field]:(field==='imageId'?'sha256:':'')+'0'.repeat(64)}};
      }}),/PILOT_NATIVE_CLEANUP_IDENTITY_MISMATCH/);
      assert.equal(x.state.queries.length,0);
      assert.throws(()=>x.run(),/PILOT_RECOVERY_ALREADY_CLAIMED/);
    });
  }
});
test('wrong container identity or engine cannot mutate',async t=>{
  for(const field of ['Id','Name','Image','attemptLabel','policyLabel','engine','listedId']){
    await t.test(field,sub=>{
      const x=active(sub);
      if(field==='engine')x.state.engine='other-engine';
      else if(field==='listedId')x.docker.list=()=> '0'.repeat(64);
      else if(field==='attemptLabel')x.actual.Config.Labels[expected.label]='wrong';
      else if(field==='policyLabel')x.actual.Config.Labels['codingassistant.pilot-policy']='wrong';
      else x.actual[field]='wrong';
      assert.throws(()=>x.run(),/PILOT_(CONTAINER_OWNERSHIP_MISMATCH|ENGINE_CHANGED)/);
      assert.equal(x.state.queries.length,0);
      assert.equal(readPilotAttempt(x.root).current.stage,'Created');
    });
  }
});
test('ambiguous create without a durable ID remains blocked',t=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-native-ambiguous-'));
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  const journal=reservePilotAttempt(root,expected);journal.advance('Creating');
  const x=simulatedDocker();
  assert.throws(()=>recoverExactAttemptWithHost(value,{root,host:x.docker,recover:deadOwner}),/PILOT_CREATE_OUTCOME_UNCERTAIN/);
  assert.equal(x.state.queries.length,0);
  assert.equal(readPilotAttempt(root).current.stage,'Creating');
});
test('failed lookup, removal and remaining container never establish cleanup',async t=>{
  for(const failure of ['lookup','remove','remaining','post-remove-lookup']){
    await t.test(failure,sub=>{
      const x=active(sub);
      if(failure==='lookup')x.docker.list=()=>{throw Error('QUERY_FAILED');};
      if(failure==='remove')x.docker.query=()=>{throw Error('REMOVE_FAILED');};
      if(failure==='remaining')x.state.keep=true;
      if(failure==='post-remove-lookup')x.docker.list=()=>{
        if(!x.state.present)throw Error('QUERY_FAILED');return id;
      };
      assert.throws(()=>x.run(),/QUERY_FAILED|REMOVE_FAILED|PILOT_CLEANUP_UNCONFIRMED/);
      assert.ok(readPilotAttempt(x.root));
      assert.throws(()=>readArchivedPilotAttempt(x.root,value.attemptId));
      if(failure!=='lookup'){
        assert.equal(readPilotAttempt(x.root).current.stage,'CleanupPending');
        // The historical lock is intentionally not recycled after interruption.
        // A remaining container is not sufficient authority to replay cleanup.
        assert.equal(fs.existsSync(path.join(x.root,'active','recovery.lock')),true);
      }
    });
  }
});
test('already cleaned active journal is archived without issuing removal',t=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-native-cleaned-'));
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  const journal=reservePilotAttempt(root,expected);
  journal.advance('Creating');journal.advance('Created',id);
  journal.advance('CleanupPending');journal.advance('Cleaned');
  const x=simulatedDocker();x.state.present=false;
  const result=recoverExactAttemptWithHost(value,{root,host:x.docker,recover:deadOwner});
  assert.equal(result.containerRemoved,true);
  assert.equal(x.state.queries.length,0);
  assert.equal(readPilotAttempt(root),null);
});
