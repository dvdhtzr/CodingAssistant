#!/usr/bin/env node
// Owner-selected source-only ZIP -> isolated, no-host-mount Docker browser smoke.
// Intentionally does NOT qualify Chromium's inner sandbox or verify a project.
// Chromium --no-sandbox is used solely INSIDE an ephemeral hardened container.
// No cloud, no token, no repo modifications, no generated code on host.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {inflateRawSync} from 'node:zlib';
import {
  IMAGE_TAG,validateDockerContext,validatePinnedImage,
  shellDocker,writePrivateEvidence,classifyGuestFailure,
} from './windows_docker_qualification.mjs';

const GUEST=fs.readFileSync(
  new URL('./generated_zip_smoke_guest.mjs',import.meta.url),'utf8');
const INSERT="'__CA_UNTRUSTED_SNAPSHOT_BASE64__'";
const EXPECTED=Object.freeze([
  'exact_source_hashes','non_root','no_host_mounts','root_write_denied',
  'network_denied','javascript_syntax','local_assets_linked',
  'browser_rendered','source_integrity_unchanged',
]);
const NAME=/^ca-smoke-[0-9a-f]{12}$/;
const IMAGE=/^sha256:[0-9a-f]{64}$/;
const SAFE=/^(?:[A-Za-z0-9_-]+\/){0,3}[A-Za-z0-9_-]+\.(?:html|css|js|json)$/;
function fail(code){throw new Error(code);}
function sha(buffer){return createHash('sha256').update(buffer).digest('hex');}
function crc32(bytes){
  let crc=0xffffffff;
  for(const byte of bytes){
    crc^=byte;
    for(let i=0;i<8;i++)
      crc=crc&1?(crc>>>1)^0xedb88320:crc>>>1;
  }
  return (crc^0xffffffff)>>>0;
}
function within(buf,at,length){
  return Number.isSafeInteger(at)&&Number.isSafeInteger(length)&&at>=0&&
    length>=0&&at+length<=buf.length;
}
function u16(buf,offset){
  if(!within(buf,offset,2))fail('ZIP_TRUNCATED');
  return buf.readUInt16LE(offset);
}
function u32(buf,offset){
  if(!within(buf,offset,4))fail('ZIP_TRUNCATED');
  return buf.readUInt32LE(offset);
}
function nameOf(data){
  try{return new TextDecoder('utf-8',{fatal:true}).decode(data);}
  catch{fail('ZIP_INVALID_UTF8');}
}
export function parseTrustedStaticZip(buf){
  if(!Buffer.isBuffer(buf)||buf.length<22||buf.length>2*1024*1024)
    fail('ZIP_SIZE_INVALID');
  let end=-1;
  for(let i=buf.length-22;i>=Math.max(0,buf.length-65557);i--){
    if(u32(buf,i)===0x06054b50 && i+22+u16(buf,i+20)===buf.length){
      end=i;break;
    }
  }
  if(end<0)fail('ZIP_CENTRAL_DIRECTORY_MISSING');
  if(u16(buf,end+4)!==0||u16(buf,end+6)!==0||
     u16(buf,end+8)!==u16(buf,end+10))
    fail('ZIP_MULTIDISK_NOT_ALLOWED');
  const count=u16(buf,end+10),length=u32(buf,end+12);
  const start=u32(buf,end+16);
  if(count<1||count>12||start===0xffffffff||
     length===0xffffffff||!within(buf,start,length)||
     start+length>end)fail('ZIP_MANIFEST_INVALID');
  let cursor=start,total=0;const files=[],seen=new Set();
  for(let n=0;n<count;n++){
    if(!within(buf,cursor,46)||u32(buf,cursor)!==0x02014b50)
      fail('ZIP_ENTRY_INVALID');
    const flags=u16(buf,cursor+8),method=u16(buf,cursor+10);
    const crc=u32(buf,cursor+16),compressed=u32(buf,cursor+20);
    const plain=u32(buf,cursor+24);
    const nlen=u16(buf,cursor+28),elen=u16(buf,cursor+30);
    const comment=u16(buf,cursor+32),disk=u16(buf,cursor+34);
    const mode=(u32(buf,cursor+38)>>>16)&0xffff;
    const local=u32(buf,cursor+42);
    if(!within(buf,cursor+46,nlen+elen+comment) ||
      disk!==0||(flags&~(0x0800|0x0008))!==0 ||
      ![0,8].includes(method) || compressed>512000 ||
      plain<1||plain>65536||local>=start||local===0xffffffff)
      fail('ZIP_ENTRY_UNSUPPORTED');
    if(mode!==0 && (mode&0xf000)!==0 && (mode&0xf000)!==0x8000)
      fail('ZIP_NONREGULAR_ENTRY');
    const name=nameOf(buf.subarray(cursor+46,cursor+46+nlen));
    cursor+=46+nlen+elen+comment;
    if(!SAFE.test(name)||name.length>140||seen.has(name.toLowerCase())||
       name.split('/').some(part=>['node_modules','bin','obj','artifacts']
         .includes(part.toLowerCase())))
      fail('ZIP_UNSAFE_OR_DUPLICATE_PATH');
    seen.add(name.toLowerCase());
    if(u32(buf,local)!==0x04034b50||
       u16(buf,local+8)!==method||
       u16(buf,local+6)!==flags)fail('ZIP_HEADER_MISMATCH');
    const lname=u16(buf,local+26),lextra=u16(buf,local+28);
    if(!within(buf,local+30,lname+lextra) ||
       nameOf(buf.subarray(local+30,local+30+lname))!==name)
      fail('ZIP_HEADER_MISMATCH');
    const offset=local+30+lname+lextra;
    if(!within(buf,offset,compressed)||offset+compressed>start)
      fail('ZIP_PAYLOAD_OUT_OF_BOUNDS');
    let value;
    try{
      value=method===0?buf.subarray(offset,offset+compressed):
        inflateRawSync(buf.subarray(offset,offset+compressed),{
          maxOutputLength:65537,
        });
    }catch{fail('ZIP_DECOMPRESSION_FAILED');}
    if(value.length!==plain||crc32(value)!==crc)
      fail('ZIP_CHECKSUM_FAILED');
    total+=value.length;
    if(total>160000)fail('ZIP_TOTAL_SOURCE_EXCEEDED');
    const content=nameOf(value);
    if(content.includes('\0')||content.length<4)
      fail('ZIP_SOURCE_INVALID');
    files.push({path:name,content,sha256:sha(value)});
  }
  if(cursor!==start+length||!seen.has('index.html'))
    fail('ZIP_ENTRYPOINT_OR_DIRECTORY_INVALID');
  return files.sort((a,b)=>a.path.localeCompare(b.path,'en'));
}
export function dockerArgs(imageId,name){
  if(!IMAGE.test(imageId)||!NAME.test(name))fail('INVALID_DOCKER_ID');
  return [
    'run','--rm','-i','--pull=never','--name',name,
    '--label','codingassistant.source-smoke='+name,
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2',
    '--ipc=private','--shm-size=256m','--init',
    '--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m',
    '--env=HOME=/tmp','--env=XDG_CONFIG_HOME=/tmp',
    '--env=XDG_CACHE_HOME=/tmp',
    '--entrypoint=node',imageId,'--input-type=module',
    '-','--run-owner-smoke',
  ];
}
export function parseResult(raw){
  if(typeof raw!=='string'||raw.length<2||raw.length>3000)
    fail('GUEST_REPORT_INVALID');
  let report;
  try{report=JSON.parse(raw.trim());}
  catch{fail('GUEST_REPORT_INVALID');}
  const c=report?.checks;
  if(report?.schemaVersion!==1||
     report.browserSandbox!=='DISABLED_CONTAINER_ONLY' ||
     !c||typeof c!=='object'||Array.isArray(c)||
     Object.keys(c).length!==EXPECTED.length ||
     !EXPECTED.every(k=>Object.hasOwn(c,k)&&typeof c[k]==='boolean')||
     Object.keys(report).length!==3)
    fail('GUEST_REPORT_SCHEMA_INVALID');
  return Object.fromEntries(EXPECTED.map(k=>[k,c[k]]));
}
function metadata(docker,args,label){
  const r=docker(args,9000);
  if(r.status!==0||!r.stdout)fail(label+'_UNAVAILABLE');
  try{return JSON.parse(r.stdout.trim());}
  catch{fail(label+'_BAD_RESPONSE');}
}
export function smokeZip({
  zip,platform=process.platform,env=process.env,docker=shellDocker,
  name='ca-smoke-'+randomBytes(6).toString('hex'),
}={}){
  const result={
    schemaVersion:1,kind:'OWNER_INVOKED_DOCKER_SOURCE_SMOKE',
    status:'BLOCKED',recordedAtUtc:new Date().toISOString(),
    imageTag:IMAGE_TAG,imageId:null,
    archiveSha256:null,sourceSha256:null,sourceFiles:0,
    checks:null,passed:0,required:EXPECTED.length,
    containerRemoved:false,modelInvoked:false,
    browserSandbox:'DISABLED_CONTAINER_ONLY',
    dockerNetwork:'none',hostMounts:0,
    independentFunctionalVerification:false,
    runtimeProfileQualified:false,generatedArtifactVerified:false,
    reason:null,
  };
  if(platform!=='win32'||!NAME.test(name)||env.DOCKER_HOST||env.DOCKER_CONTEXT){
    result.reason='LOCAL_WINDOWS_DOCKER_ONLY';return result;
  }
  let launched=false;
  try{
    if(typeof zip!=='string'||!zip.toLowerCase().endsWith('.zip'))
      fail('EXPLICIT_ZIP_REQUIRED');
    const stat=fs.lstatSync(zip);
    if(!stat.isFile()||stat.isSymbolicLink()||stat.size>2*1024*1024)
      fail('ZIP_IS_NOT_REGULAR_FILE');
    const archive=fs.readFileSync(zip);
    const files=parseTrustedStaticZip(archive);
    result.archiveSha256=sha(archive);
    result.sourceSha256=sha(Buffer.from(JSON.stringify(
      files.map(f=>({path:f.path,sha256:f.sha256})))));
    result.sourceFiles=files.length;
    const selected=docker(['context','show'],8000);
    if(selected.status!==0||!selected.stdout)fail('DOCKER_CONTEXT_UNAVAILABLE');
    const current=selected.stdout.trim();
    const endpoint=metadata(docker,['context','inspect',current,
      '--format','{{json .}}'],'DOCKER_CONTEXT');
    const info=metadata(docker,['info','--format','{{json .}}'],'DOCKER_INFO');
    validateDockerContext(current,endpoint?.Endpoints?.docker?.Host,info);
    const pin=validatePinnedImage(metadata(docker,[
      'image','inspect',IMAGE_TAG,'--format','{{json .}}'],'PINNED_IMAGE'));
    result.imageId=pin.imageId;
    const collision=docker(['ps','-a','--filter','name=^/'+name+'$',
      '--format','{{.ID}}'],8000);
    if(collision.status!==0||collision.stdout.trim())fail('DOCKER_NAME_COLLISION');
    const encoded=Buffer.from(JSON.stringify({schemaVersion:1,files}))
      .toString('base64');
    if(GUEST.split(INSERT).length!==2)fail('GUEST_SOURCE_INTEGRITY_FAILURE');
    const payload=GUEST.replace(INSERT,JSON.stringify(encoded));
    launched=true;
    const guest=docker(dockerArgs(pin.imageId,name),95000,payload);
    if(guest.timedOut)fail('DOCKER_GUEST_TIMEOUT');
    if(!guest.stdout||!guest.stdout.trim())
      fail('DOCKER_GUEST_NO_OUTPUT_'+classifyGuestFailure(guest));
    const checks=parseResult(guest.stdout);
    result.checks=checks;
    result.passed=EXPECTED.filter(key=>checks[key]).length;
    const environmental=[
      'exact_source_hashes','non_root','no_host_mounts',
      'root_write_denied','network_denied','source_integrity_unchanged',
    ];
    if(!environmental.every(key=>checks[key]))
      fail('CONTAINMENT_CHECK_FAILED');
    if(guest.status!==0||result.passed!==EXPECTED.length){
      result.status='SMOKE_TEST_FAILED';
      result.reason='SOURCE_SMOKE_CHECKS_FAILED';
    }else result.status='SMOKE_PASS_NOT_VERIFIED';
  }catch(e){
    result.status='BLOCKED';
    // Host parser emits predeclared bounded codes. Never persist raw
    // child stderr, generated file content, or the selected host path.
    result.reason=String(e?.message||'SMOKE_BLOCKED').slice(0,120);
  }finally{
    if(launched){
      try{
        const found=docker(['container','inspect',name,
          '--format','{{json .Config.Labels}}'],15000);
        if(found.status===0){
          const labels=JSON.parse(found.stdout.trim());
          if(labels?.['codingassistant.source-smoke']!==name)
            fail('CONTAINER_OWNERSHIP_MISMATCH');
          docker(['rm','-f',name],15000);
        }
        const left=docker(['ps','-a','--filter','name=^/'+name+'$',
          '--format','{{.ID}}'],15000);
        result.containerRemoved=left.status===0&&left.stdout.trim()==='';
      }catch{result.containerRemoved=false;}
      if(!result.containerRemoved){
        result.status='BLOCKED';
        result.reason='CONTAINER_CLEANUP_UNCONFIRMED';
      }
    }
  }
  return result;
}
export function main(argv=process.argv.slice(2),options={}){
  if(argv.length===0){
    process.stdout.write('STATIC WEB SOURCE SMOKE: NOT_RUN\n');
    process.stdout.write('Use --zip <path> to test an owner-selected source ZIP.\n');
    return 0;
  }
  const jsonMode=argv.length===3&&argv[2]==='--json';
  if((argv.length!==2&&!jsonMode)||argv[0]!=='--zip'){
    process.stderr.write('Usage: node tools/m4/smoke_generated_zip.mjs --zip <file.zip> [--json]\n');
    return 2;
  }
  const result=smokeZip({...options,zip:argv[1]});
  if(jsonMode){
    // Authenticated local API consumes this bounded machine-readable report.
    // No path, source text, Docker log, or VERIFIED claim is returned.
    process.stdout.write(JSON.stringify(result)+'\n');
    return result.status==='SMOKE_PASS_NOT_VERIFIED'&&result.containerRemoved?0:2;
  }
  const evidence=writePrivateEvidence(result,options.evidenceFolder);
  process.stdout.write('STATIC WEB '+result.status+'\n');
  process.stdout.write('  container-only Chromium sandbox: DISABLED (Docker isolation)\n');
  process.stdout.write('  source-specific checks: '+result.passed+'/'+result.required+'\n');
  process.stdout.write('  container removed: '+result.containerRemoved+'\n');
  if(result.checks){
    const failed=Object.keys(result.checks).filter(k=>!result.checks[k]);
    if(failed.length)process.stdout.write('  failing checks: '+failed.join(', ')+'\n');
  }
  if(result.reason)process.stdout.write('  reason: '+result.reason+'\n');
  process.stdout.write('  acceptance: SMOKE ONLY; complete QA: NOT VERIFIED\n');
  process.stdout.write('Evidence: '+evidence+'\n');
  return result.status==='SMOKE_PASS_NOT_VERIFIED'&&result.containerRemoved?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=main();
