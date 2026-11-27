#!/usr/bin/env node
// Fixed-source dispatcher qualification only. This is the exact local
// host subprocess boundary, NOT a generated-source dispatch interface.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {qualifyPilotLifecycle,pilotHostTools,pilotLocalRoot} from './windows_pilot_lifecycle_qualification.mjs';
import {readLocalImage} from './static_web_evaluation_host_bridge.mjs';
import {buildQualifiedCandidateBundle,buildQualificationFixture} from './static_web_build_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {readArchivedPilotAttempt,readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {parseFixedCandidateStream} from './windows_candidate_fixture_qualification.mjs';
import {writePrivateEvidence} from './windows_docker_qualification.mjs';
const fail=code=>{throw Error(code);};
const sha=x=>createHash('sha256').update(x).digest('hex');
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const hex32=x=>typeof x==='string'&&x.length===32&&/^[a-f0-9]{32}$/.test(x);
const equal=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const label='codingassistant.static-web-pilot-qualification';
export function strictDispatchObservations(raw,expected){
  if(typeof raw!=='string'||raw.length>32000)fail('PILOT_DISPATCH_STREAM_INVALID');
  const lines=raw.trimEnd().split(/\r?\n/);
  const stages=['START','SECURITY','BUILD','SANDBOX','COMPLETE'];
  // Working reference only. The build failure and other negative
  // controls have their own existing qualification receipts.
  if(lines.length!==stages.length+1||stages.some((stage,i)=>{
    try{const p=JSON.parse(lines[i]);return Object.keys(p).length!==2||p.kind!=='progress'||p.stage!==stage;}
    catch{return true;}
  }))fail('PILOT_DISPATCH_STREAM_INVALID');
  const result=parseFixedCandidateStream(raw,expected).observations;
  if(result?.status!=='CHECKS_PASSED_NOT_VERIFIED'||result.passed!==7||result.completed!==7)
    fail('PILOT_DISPATCH_RESULT_INVALID');
  return result;
}
function pinnedFile(file,expected,basename){
  if(typeof file!=='string'||!path.isAbsolute(file)||!digest(expected)||
    path.basename(file).toLowerCase()!==basename)fail('PILOT_DISPATCH_PIN_REQUIRED');
  try{
    const canonical=fs.realpathSync(file);
    if(canonical.toLowerCase()!==path.resolve(file).toLowerCase()||!fs.lstatSync(file).isFile()||
      sha(fs.readFileSync(file))!==expected)fail('PILOT_DISPATCH_PIN_CHANGED');
    return canonical;
  }catch{fail('PILOT_DISPATCH_PIN_CHANGED');}
}
// No PATH lookup, shell, command text or Docker endpoint overrides.
export function pinnedDispatchDocker(env=process.env){
  if(process.platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)
    fail('PILOT_LOCAL_WINDOWS_ONLY');
  const check=()=>{
    pinnedFile(process.execPath,env.CA_PILOT_NODE_SHA256,'node.exe');
    return pinnedFile(env.CA_PILOT_DOCKER_EXE,env.CA_PILOT_DOCKER_SHA256,'docker.exe');
  };
  check();
  return (args,timeout,input)=>{
    const exe=check();
    const result=spawnSync(exe,args,{encoding:'utf8',timeout,maxBuffer:64*1024,input,
      windowsHide:true,shell:false,env});
    check();
    return {status:result.status,stdout:result.stdout||'',stderr:result.stderr||'',
      timedOut:result.error?.code==='ETIMEDOUT',errorCode:result.error?.code??null};
  };
}
export function qualifyPinnedDispatch({platform=process.platform,env=process.env,docker,root,
  attemptId=randomBytes(16).toString('hex')}={}){
  const result={schemaVersion:1,stage:'AET-8-pinned-dispatch-control',status:'BLOCKED',
    recordedAtUtc:new Date().toISOString(),attemptId,engineId:null,policySha256:null,
    adapterSha256:null,transportSha256:null,nodeSha256:env.CA_PILOT_NODE_SHA256??null,
    dockerSha256:env.CA_PILOT_DOCKER_SHA256??null,observations:null,archiveSha256:null,
    configurationAudited:false,containerRemoved:false,replayBlocked:false,cleanupDebt:false,
    generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,runtimeQualified:false,
    generatedArtifactVerified:false,reason:null};
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){result.reason='PILOT_LOCAL_WINDOWS_ONLY';return result;}
  try{
    if(!hex32(attemptId))fail('PILOT_DISPATCH_ATTEMPT_INVALID');
    docker??=pinnedDispatchDocker(env);
    if(!digest(result.nodeSha256)||!digest(result.dockerSha256))fail('PILOT_DISPATCH_PIN_REQUIRED');
    root??=pilotLocalRoot(env);
    const host=pilotHostTools(docker),engineId=host.engine();
    const image=readLocalImage({platform,env,docker}),bundle=buildQualifiedCandidateBundle();
    const policy=preparePilotPolicy(image,bundle.adapterSha256,bundle.evaluatorSha256);
    const fixture=buildQualificationFixture('build-working',bundle,policy.policySha256,attemptId);
    Object.assign(result,{engineId,policySha256:policy.policySha256,adapterSha256:bundle.adapterSha256,
      transportSha256:sha(fixture.source)});
    let creates=0,starts=0,attached=null,observations=null;
    const commands=(args,timeout,input)=>{
      // Only the known metadata queries and the exact policy-bound
      // create/start/removal for this fixed attempt may reach Docker.
      if(!Array.isArray(args)||!args.every(a=>typeof a==='string')||
        !Number.isSafeInteger(timeout)||timeout<1||timeout>180000)fail('PILOT_DISPATCH_COMMAND_INVALID');
      if(args[0]==='create'){
        if(creates++!==0||input!==undefined||!equal(args,policy.createArgs(attemptId)))
          fail('PILOT_DISPATCH_COMMAND_INVALID');
        const reply=docker(args,timeout);
        const id=reply?.stdout?.trim();
        if(reply?.status===0&&digest(id))attached=id;
        return reply;
      }
      if(args[0]==='start'){
        if(starts++!==0||!attached||!equal(args,['start','-a','-i',attached])||
          input!==fixture.source||sha(input)!==result.transportSha256)
          fail('PILOT_DISPATCH_COMMAND_INVALID');
        const reply=docker(args,timeout,input);
        if(reply?.status===0&&!reply.timedOut&&!reply.errorCode)
          observations=strictDispatchObservations(reply.stdout,fixture.expected);
        return reply;
      }
      if(args[0]==='rm'){
        // A lost create response is deliberately not guessed at here.
        if(!attached||!equal(args,['rm','-f',attached])||input!==undefined)
          fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else if(args[0]==='container'){
        if(!attached||!equal(args,['container','inspect',attached,'--format','{{json .}}']))
          fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else if(args[0]==='ps'){
        if(args.length!==7||args[1]!=='-a'||args[2]!=='--no-trunc'||args[3]!=='--filter'||
          !['label='+label,'name=^/ca-win-pilot-'+attemptId+'$',...(attached?['id='+attached]:[])].includes(args[4])||
          args[5]!=='--format'||args[6]!=='{{.ID}}')fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else if(args[0]==='context'){
        if(!equal(args,['context','show'])&&!(args.length===5&&args[1]==='inspect'&&
          /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,70}$/.test(args[2])&&args[3]==='--format'&&args[4]==='{{json .}}'))
          fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else if(args[0]==='info'){
        if(!equal(args,['info','--format','{{json .}}']))fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else if(args[0]==='image'){
        // The existing local image selector is fixed, never user supplied.
        if(!equal(args,['image','inspect','mcr.microsoft.com/playwright:v1.65.1-noble-amd64','--format','{{json .}}']))
          fail('PILOT_DISPATCH_COMMAND_INVALID');
      }else fail('PILOT_DISPATCH_COMMAND_INVALID');
      if(input!==undefined)fail('PILOT_DISPATCH_COMMAND_INVALID');
      return docker(args,timeout);
    };
    const options={platform,env,docker:commands,root,attemptId,control:'build-working'};
    const run=qualifyPilotLifecycle(options);
    Object.assign(result,{configurationAudited:run.configurationAudited,containerRemoved:run.containerRemoved,
      cleanupDebt:run.cleanupDebt,observations:run.observations});
    if(run.status!=='PILOT_BUILD_CONTROL_PASS'||run.policySha256!==policy.policySha256||
      run.engineId!==engineId||creates!==1||starts!==1||!observations||
      !equal(observations,run.observations)||!run.containerRemoved||run.cleanupDebt)
      fail(run.reason??'PILOT_DISPATCH_RESULT_INVALID');
    const archive=readArchivedPilotAttempt(root,attemptId);
    if(archive.identity.engineId!==engineId||archive.identity.policySha256!==policy.policySha256||
      archive.identity.candidateSha256!==fixture.expected.candidateSha256||
      archive.identity.archiveSha256!==fixture.expected.archiveSha256||archive.recoveryClaimed||
      !equal(archive.events.map(e=>e.stage),['Reserved','Creating','Created','Audited','Starting','Exited','CleanupPending','Cleaned'])||
      archive.events.at(-1).containerId!==attached)
      fail('PILOT_DISPATCH_ARCHIVE_INVALID');
    result.archiveSha256=sha(JSON.stringify(archive));
    const replay=qualifyPilotLifecycle(options);
    if(replay.reason!=='PILOT_ATTEMPT_REPLAY'||creates!==1||starts!==1||replay.cleanupDebt||
      host.engine()!==engineId||readPilotAttempt(root)!==null||host.list('label='+label)!=='')
      fail('PILOT_DISPATCH_REPLAY_UNCONFIRMED');
    result.replayBlocked=true;result.status='PILOT_PINNED_DISPATCH_PASS';
  }catch(e){result.reason=/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_DISPATCH_BLOCKED';}
  finally{
    if(root)try{result.cleanupDebt=result.cleanupDebt||readPilotAttempt(root)!==null||
      pilotHostTools(docker).list('label='+label)!=='';}catch{result.cleanupDebt=true;}
    if(result.cleanupDebt)result.status='BLOCKED';
  }
  return result;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('PILOT_PINNED_DISPATCH NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run-fixed-control'){console.error('Only --run-fixed-control is supported');return 2;}
  const result=qualifyPinnedDispatch();
  console.log('AET-8 WINDOWS_'+result.status);
  console.log('  audited: '+result.configurationAudited+'; removed: '+result.containerRemoved+
    '; replay blocked: '+result.replayBlocked+'; cleanup debt: '+result.cleanupDebt);
  if(result.reason)console.log('  blocker: '+result.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(result));
  return result.status==='PILOT_PINNED_DISPATCH_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
