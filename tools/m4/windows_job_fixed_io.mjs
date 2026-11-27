// Fixed job-owned request/response qualification. No generated input,
// paths, executable commands, or caller-supplied policy are accepted.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {qualifyCandidateDispatchControl,main as dispatchMain} from './static_web_candidate_dispatch.mjs';
const controls=['build-working','build-spoof','build-missing-asset'];
const fail=code=>{throw Error(code);};
export function parseFixedIoRequest(raw){
  if(typeof raw!=='string'||Buffer.byteLength(raw)>4096)fail('PILOT_JOB_REQUEST_INVALID');
  const value=JSON.parse(raw);
  // Exactly one fixed interruption request; no caller-selected identity or input.
  if(value&&typeof value==='object'&&!Array.isArray(value)&&Object.keys(value).length===1&&
    value.operation==='fixed-interruption')return Object.freeze({operation:value.operation});
  if(!value||typeof value!=='object'||Array.isArray(value)||
    Object.keys(value).length!==3||value.operation!=='fixed-control'||
    !controls.includes(value.control)||typeof value.attemptId!=='string'||
    value.attemptId.length!==32||!/^[a-f0-9]{32}$/.test(value.attemptId))
    fail('PILOT_JOB_REQUEST_INVALID');
  return Object.freeze({operation:value.operation,control:value.control,attemptId:value.attemptId});
}
export async function main(args=process.argv.slice(2)){
  if(!args.length){console.log('PILOT_JOB_FIXED_IO NOT_RUN');return 0;}
  try{
    if(args.length!==1||args[0]!=='--stdin-fixed')fail('PILOT_JOB_REQUEST_INVALID');
    if(Object.keys(process.env).some(k=>/^NODE_|^OPENSSL_(CONF|MODULES)$/i.test(k)&&process.env[k]))
      fail('PILOT_DISPATCH_ENVIRONMENT_OVERRIDE');
    const bytes=Buffer.alloc(4097);let size=0;
    while(size<bytes.length){const n=fs.readSync(0,bytes,size,bytes.length-size,null);if(!n)break;size+=n;}
    if(size>4096)fail('PILOT_JOB_REQUEST_INVALID');
    const request=parseFixedIoRequest(new TextDecoder('utf-8',{fatal:true}).decode(bytes.subarray(0,size)));
    if(request.operation==='fixed-interruption'){
      // Reuse the historical frozen held-stdin control, without changing its pinned closure.
      // A normal return is always blocked; the observer must interrupt this process.
      await dispatchMain(['--run-job-crash-control']);
      fail('PILOT_JOB_OWNER_NOT_INTERRUPTED');
    }
    const result=await qualifyCandidateDispatchControl({control:request.control,attemptId:request.attemptId});
    console.log(JSON.stringify(result));
    return result.qualificationPassed?0:2;
  }catch(e){
    console.log(JSON.stringify({status:'BLOCKED',reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_JOB_REQUEST_INVALID',
      executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false}));
    return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=await main();
