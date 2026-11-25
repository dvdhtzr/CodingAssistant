import test from 'node:test';
import assert from 'node:assert/strict';
import {PassThrough} from 'node:stream';
import {EventEmitter} from 'node:events';
import {spawnSync} from 'node:child_process';
import {trustedDockerArgs,parseTrustedReport,parseGuestStream,trustedPayload,qualifyTrusted} from '../tools/m4/windows_aet8_trusted_qualification.mjs';
import {SECURITY_CASES,FIXTURE_CASES} from '../tools/m4/windows_aet8_trusted_guest.mjs';
import {QUALIFICATION_BROWSER_ARGS,createPipeTransport,waitForBrowserExit,closeBrowserProcess} from '../tools/m4/windows_aet8_pipe_driver.mjs';
import {CALCULATION_CASES,evaluateCalculationJournal} from '../tools/m4/calculation_journal_contract.mjs';
const name='ca-win-aet8-012345abcdef',id='sha256:'+'a'.repeat(64);
const booleans=(keys,value)=>Object.fromEntries(keys.map(k=>[k,value]));
const good=()=>({schemaVersion:1,security:booleans(SECURITY_CASES,true),fixture:booleans(FIXTURE_CASES,true),observations:{working:booleans(CALCULATION_CASES,true),broken:booleans(CALCULATION_CASES,false),spoof:booleans(CALCULATION_CASES,false)}});
function dockerMock(change={}){
  const calls=[];let ps=0;
  return {calls,docker(args,timeout,input){
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')return {status:0,stdout:'desktop-linux'};
    if(args[0]==='context')return {status:0,stdout:JSON.stringify({Endpoints:{docker:{Host:change.remote?'tcp://remote':'npipe:////./pipe/dockerDesktopLinuxEngine'}}})};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify({OSType:'linux',OperatingSystem:'Docker Desktop',PidsLimit:true,MemoryLimit:true})};
    if(args[0]==='image')return {status:0,stdout:JSON.stringify({Id:id,RepoDigests:['mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64)],Os:'linux',Architecture:'amd64'})};
    if(args[0]==='ps')return {status:0,stdout:++ps===1?(change.collision?'id':''):(change.leftover?'id':'')};
    if(args[0]==='run')return {status:change.exit??0,stdout:change.stdout??JSON.stringify(change.report??good()),timedOut:change.timeout??false};
    if(args[0]==='container')return {status:0,stdout:JSON.stringify({'codingassistant.aet8':change.badLabel?'wrong':name})};
    if(args[0]==='rm')return {status:0,stdout:''};
    throw Error('Unexpected command');
  }};
}
test('trusted integrated profile reuses only approved fixture restrictions',()=>{
  const args=trustedDockerArgs(id,name);
  for(const a of ['--network=none','--read-only','--cap-drop=all','--security-opt=no-new-privileges','--user=1000:1000','--pids-limit=256','--memory=2g','--cpus=2','--ipc=private','--init','--run-trusted-aet8'])assert.ok(args.includes(a),a);
  for(const a of ['--mount','-v','--cap-add','--privileged','--publish','--network=host'])assert.ok(!args.includes(a),a);
  assert.ok(QUALIFICATION_BROWSER_ARGS.includes('--remote-debugging-pipe'));
  assert.ok(!QUALIFICATION_BROWSER_ARGS.some(a=>/no-sandbox|disable-setuid-sandbox|disable-web-security|remote-debugging-port|allow-chrome-scheme-url|single-process|no-zygote/.test(a)));
  assert.throws(()=>trustedDockerArgs(id,'ca-win-m47-012345abcdef'));
  const payload=trustedPayload();assert.equal(Object.keys(payload.hashes).length,4);
  assert.ok(!payload.source.includes('__CA_AET8_FIXED_PAYLOAD__'));
  for(const file of ['windows_aet8_pipe_driver.mjs','windows_aet8_trusted_guest.mjs','windows_aet8_trusted_qualification.mjs']){
    const x=spawnSync(process.execPath,['--check','tools/m4/'+file],{encoding:'utf8'});assert.equal(x.status,0,x.stderr);
  }
});
test('positive trusted receipts are never generated-source permission',()=>{
  const m=dockerMock();const result=qualifyTrusted({platform:'win32',env:{},name,docker:m.docker});
  assert.equal(result.status,'TRUSTED_INTEGRATED_EVALUATOR_PASS',JSON.stringify(result));
  assert.equal(result.containerRemoved,true);
  for(const k of ['runtimeQualified','generatedSourceExecuted','generatedArtifactVerified','generatedCodeExecutionEnabled'])assert.equal(result[k],false);
  assert.equal(m.calls.filter(x=>x.args[0]==='run').length,1);
  assert.equal(m.calls.filter(x=>x.args[0]==='rm').length,1);
});
test('failed isolation, mismatched observations, timeout or cleanup never pass',()=>{
  const bad=good();bad.security.symlink_write_denied=false;
  const forged=good();forged.observations.spoof.mouse_2_plus_3_equals_5=true;
  for(const config of [{report:bad},{report:forged},{exit:2},{timeout:true},{leftover:true},{badLabel:true},{remote:true},{collision:true}]){
    const m=dockerMock(config);const r=qualifyTrusted({platform:'win32',env:{},name,docker:m.docker});
    assert.equal(r.status,'BLOCKED',JSON.stringify(config));assert.equal(r.generatedCodeExecutionEnabled,false);
  }
  const missing=good();missing.observations.broken=null;
  assert.throws(()=>parseTrustedReport(JSON.stringify(missing)));
  assert.throws(()=>parseTrustedReport(JSON.stringify({...good(),verified:true})));
});
test('private CDP pipe handles split NUL frames and rejects protocol failures',async()=>{
  const child=new EventEmitter();child.stdio=[null,null,null,new PassThrough(),new PassThrough()];child.kill=()=>{};
  let request='';child.stdio[3].on('data',c=>{request+=c.toString();});
  const pipe=createPipeTransport(child);
  const first=pipe.send('Browser.getVersion');
  const sent=JSON.parse(request.slice(0,-1));assert.equal(sent.method,'Browser.getVersion');
  child.stdio[4].write('{"id":'+sent.id+',');child.stdio[4].write('"result":{"product":"Chrome"}}\0');
  assert.deepEqual(await first,{product:'Chrome'});
  request='';const second=pipe.send('Page.navigate',{url:'about:blank'},'session');
  const req=JSON.parse(request.slice(0,-1));assert.equal(req.sessionId,'session');
  child.stdio[4].write(JSON.stringify({id:req.id,error:{message:'private'}})+'\0');
  await assert.rejects(second,/BROWSER_PROTOCOL_ERROR/);
  pipe.close();await assert.rejects(pipe.send('Browser.getVersion'),/BROWSER_PIPE_CLOSED/);
});

test('bounded guest stage failure preserves completed security and prior fixture observations',()=>{
  const x=good();
  x.fixture.inert_negative_rejected=false;x.fixture.forged_receipt_rejected=false;x.fixture.trusted_source_unchanged=false;
  x.observations.broken=null;x.observations.spoof=null;
  x.failure={stage:'BROKEN',code:'BROWSER_STORAGE_RESET_FAILED'};
  assert.deepEqual(parseTrustedReport(JSON.stringify(x)),x);
  const m=dockerMock({report:x,exit:2});
  const r=qualifyTrusted({platform:'win32',env:{},name,docker:m.docker});
  assert.equal(r.status,'BLOCKED');assert.equal(r.reason,'GUEST_STAGE_BLOCKED');
  assert.equal(Object.values(r.security).filter(Boolean).length,10);
  assert.deepEqual(r.guestFailure,x.failure);
  assert.equal(r.containerRemoved,true);
  const early={schemaVersion:1,security:null,fixture:booleans(FIXTURE_CASES,false),observations:{working:null,broken:null,spoof:null},failure:{stage:'MODULES',code:'GUEST_STAGE_FAILED'}};
  assert.deepEqual(parseTrustedReport(JSON.stringify(early)),early);
  assert.throws(()=>parseTrustedReport(JSON.stringify({...early,failure:{stage:'MODULES',code:'PRIVATE_RAW_ERROR'}})));
});

test('exit wait is bounded and does not require close of inherited streams',async()=>{
  const child=new EventEmitter();child.exitCode=null;child.signalCode=null;
  assert.equal(await waitForBrowserExit(child,10),false);
  assert.equal(child.listenerCount('exit'),0);
  const exit=waitForBrowserExit(child,100);
  child.exitCode=0;child.emit('exit',0,null);
  assert.equal(await exit,true);
  const closed=await waitForBrowserExit(child,10);assert.equal(closed,true);
  const streams=[new PassThrough(),new PassThrough()];
  child.stdio=streams;
  const transport={send:async()=>{},close:()=>{}};
  await closeBrowserProcess(child,transport);
  assert.ok(streams.every(s=>s.destroyed));
});
test('stream checkpoints retain exact security observations on outer timeout but cannot qualify',()=>{
  const x=good();x.fixture.working_seven_checks=false;x.fixture.inert_negative_rejected=false;
  x.fixture.forged_receipt_rejected=false;x.fixture.trusted_source_unchanged=false;
  x.observations={working:null,broken:null,spoof:null};
  const line=JSON.stringify({kind:'progress',stage:'WORKING',...x})+'\n';
  const stream=parseGuestStream(line);assert.equal(stream.final,null);
  assert.equal(stream.checkpoint.stage,'WORKING');
  const m=dockerMock({stdout:line,timeout:true});
  const r=qualifyTrusted({platform:'win32',env:{},name,docker:m.docker});
  assert.equal(r.status,'BLOCKED');assert.equal(r.reason,'FIXED_EVALUATOR_TIMEOUT');
  assert.equal(r.lastGuestStage,'WORKING');assert.equal(Object.values(r.security).filter(Boolean).length,10);
  const finished=parseGuestStream(line+JSON.stringify(good())+'\n');assert.ok(finished.final);
  assert.throws(()=>parseGuestStream(JSON.stringify(good())+'\n'+line));
  assert.throws(()=>parseGuestStream(line.repeat(33)));
});

test('forced exit or nonzero browser process exit never satisfies persistence',async()=>{
  const transport={send:async()=>{},close:()=>{}};
  const child=new EventEmitter();child.exitCode=null;child.signalCode=null;child.stdio=[];
  child.kill=()=>{child.signalCode='SIGKILL';child.emit('exit',null,'SIGKILL');};
  await assert.rejects(closeBrowserProcess(child,transport,{graceMs:10,killMs:10}),/BROWSER_GRACEFUL_EXIT_TIMEOUT/);
  const failed=new EventEmitter();failed.exitCode=1;failed.signalCode=null;failed.stdio=[];
  await assert.rejects(closeBrowserProcess(failed,transport,{graceMs:10,killMs:10}),/BROWSER_EXIT_NONZERO/);
  const signal=new EventEmitter();signal.exitCode=null;signal.signalCode='SIGABRT';signal.stdio=[];
  await assert.rejects(closeBrowserProcess(signal,transport,{graceMs:10,killMs:10}),/BROWSER_EXIT_SIGNAL/);
});
test('strict trusted qualification cannot count infrastructure exceptions as negative functional cases',async()=>{
  const broken=async()=>{throw Error('BROWSER_PIPE_CLOSED');};
  await assert.rejects(evaluateCalculationJournal(broken,{onCaseError:e=>{throw e;}}),/BROWSER_PIPE_CLOSED/);
  const observations=await evaluateCalculationJournal(broken);
  assert.equal(observations.passed,0);
  assert.equal(observations.generatedArtifactVerified,false);
});
