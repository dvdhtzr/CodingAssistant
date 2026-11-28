// Owner-host cleanup-only bridge. Requires external exclusive ownership and
// original native job absence; never accepts generated source or commands.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {recoverExactAttempt,recoveryIdentity} from './native_exact_cleanup.mjs';
export function parseRecoveryRequest(raw){
  if(typeof raw!=='string'||Buffer.byteLength(raw)>4096)throw Error('PILOT_NATIVE_CLEANUP_REQUEST_INVALID');
  const value=JSON.parse(raw);
  if(!value||Object.keys(value).length!==2||value.operation!=='recover-exact-cleanup'||
    !Object.hasOwn(value,'identity'))throw Error('PILOT_NATIVE_CLEANUP_REQUEST_INVALID');
  recoveryIdentity(value.identity);
  return value.identity;
}
function main(args){
  try{
    if(args.length!==1||args[0]!=='--stdin')throw Error('PILOT_NATIVE_CLEANUP_REQUEST_INVALID');
    if(Object.keys(process.env).some(k=>/^NODE_|^OPENSSL_(CONF|MODULES)$/i.test(k)&&process.env[k]))
      throw Error('PILOT_DISPATCH_ENVIRONMENT_OVERRIDE');
    const bytes=Buffer.alloc(4097);let size=0;
    while(size<bytes.length){const n=fs.readSync(0,bytes,size,bytes.length-size,null);if(!n)break;size+=n;}
    const identity=parseRecoveryRequest(new TextDecoder('utf-8',{fatal:true}).decode(bytes.subarray(0,size)));
    console.log(JSON.stringify(recoverExactAttempt(identity)));
    return 0;
  }catch(e){
    console.log(JSON.stringify({status:'BLOCKED',reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'PILOT_NATIVE_CLEANUP_BLOCKED'}));
    return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main(process.argv.slice(2));
