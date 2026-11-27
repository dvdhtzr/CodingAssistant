// Candidate-specific, pure in-memory asset contract. No filesystem, server,
// executable admission or browser launch. The expected binding must come
// from the independent trusted host, never the candidate application.
import {createHash} from 'node:crypto';
const sha=x=>createHash('sha256').update(x).digest('hex');
const plain=x=>x!==null&&typeof x==='object'&&!Array.isArray(x);
const exact=(x,keys)=>plain(x)&&Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const digest=x=>typeof x==='string'&&/^[a-f0-9]{64}$/.test(x)&&x.length===64;
const safePath=x=>typeof x==='string'&&x.length<=140&&
  /^(?:[A-Za-z0-9_-]+\/){0,3}[A-Za-z0-9_-]+\.(?:html|css|js|json)$/.test(x)&&
  !/[\r\n]/.test(x)&&!x.split('/').some(p=>['node_modules','bin','obj','artifacts'].includes(p.toLowerCase()));
const fail=code=>{throw Error(code);};
const types=Object.freeze({html:'text/html; charset=utf-8',css:'text/css; charset=utf-8',
  js:'application/javascript; charset=utf-8',json:'application/json; charset=utf-8'});
const order=(a,b)=>a<b?-1:a>b?1:0;
const manifestKeys=['path','sha256','utf8Bytes'];
export function createCandidateAssets(payloadJson,expected){
  if(typeof payloadJson!=='string'||Buffer.byteLength(payloadJson)>1024*1024||
    !exact(expected,['admissionSha256','candidateSha256','archiveSha256','sourceFingerprint','files'])||
    !['admissionSha256','candidateSha256','archiveSha256','sourceFingerprint'].every(k=>digest(expected[k]))||
    !Array.isArray(expected.files)||expected.files.length<1||expected.files.length>12)
    fail('CANDIDATE_ASSET_BINDING_INVALID');
  let payload;try{payload=JSON.parse(payloadJson);}catch{fail('CANDIDATE_ASSET_PAYLOAD_INVALID');}
  if(!exact(payload,['schemaVersion','admissionSha256','candidateSha256','archiveSha256','sourceFingerprint','files'])||
    payload.schemaVersion!==1||!['admissionSha256','candidateSha256','archiveSha256','sourceFingerprint'].every(k=>payload[k]===expected[k])||
    !Array.isArray(payload.files)||payload.files.length!==expected.files.length)
    fail('CANDIDATE_ASSET_BINDING_INVALID');
  const routes=new Map(),seen=new Set();let total=0;
  for(let i=0;i<payload.files.length;i++){
    const file=payload.files[i],manifest=expected.files[i];
    if(!exact(file,[...manifestKeys,'content'])||!exact(manifest,manifestKeys)||
      !safePath(file.path)||file.path!==manifest.path||file.sha256!==manifest.sha256||
      file.utf8Bytes!==manifest.utf8Bytes||!digest(file.sha256)||
      typeof file.content!=='string'||file.content.includes('\0')||
      !Number.isSafeInteger(file.utf8Bytes)||file.utf8Bytes<1||file.utf8Bytes>65536||
      Buffer.byteLength(file.content)!==file.utf8Bytes||sha(file.content)!==file.sha256||
      seen.has(file.path.toLowerCase())||(i>0&&order(expected.files[i-1].path,file.path)>=0))
      fail('CANDIDATE_ASSET_MANIFEST_INVALID');
    total+=file.utf8Bytes;if(total>160000)fail('CANDIDATE_ASSET_MANIFEST_INVALID');
    seen.add(file.path.toLowerCase());
    routes.set('/'+file.path,Object.freeze({status:200,
      contentType:types[file.path.split('.').at(-1)],body:file.content}));
  }
  const fingerprint=sha([...expected.files].sort((a,b)=>order(a.path.toUpperCase(),b.path.toUpperCase()))
    .map(f=>f.path+'\t'+f.sha256).join('\n'));
  if(fingerprint!==expected.sourceFingerprint||!routes.has('/index.html'))
    fail('CANDIDATE_ASSET_MANIFEST_INVALID');
  const missing=Object.freeze({status:404,contentType:'text/plain; charset=utf-8',body:''});
  // Exact raw path only. No normalization, decoding, fallback, redirect,
  // filesystem reads, dynamic route, or host-side application execution.
  return Object.freeze({entryPath:'/index.html',sourceFingerprint:fingerprint,
    assetCount:routes.size,resolve(method,target){
      return method==='GET'&&typeof target==='string'&&target.length<=141?
        routes.get(target)??missing:missing;
    }});
}
