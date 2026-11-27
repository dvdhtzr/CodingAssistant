// Qualification-only browser boundary control. No user input or execution
// admission. Reuses the reviewed fixed guest module closure without
// modifying its identity or invalidating prior qualification receipts.
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {fixedCandidateBundle} from './windows_candidate_fixture_qualification.mjs';
import {createCandidateAssets} from './static_web_candidate_assets.mjs';

const sha=x=>createHash('sha256').update(x).digest('hex');
const fail=code=>{throw Error(code);};
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const attempt=x=>typeof x==='string'&&x.length===32&&/^[a-f0-9]{32}$/.test(x);
function replaceOnce(source,from,to){
  if(source.split(from).length!==2)fail('BOUNDARY_BUNDLE_CHANGED');
  return source.replace(from,()=>to);
}
function moduleUrl(source,symbol){
  const pattern=new RegExp("import\\s*\\{[^}]*\\b"+symbol+"\\b[^}]*\\}\\s*from\\s*'(data:text/javascript;base64,[A-Za-z0-9+/=]+)'");
  const match=source.match(pattern);
  if(!match)fail('BOUNDARY_BUNDLE_CHANGED');
  return match[1];
}
const decode=url=>Buffer.from(url.split(',')[1],'base64').toString('utf8');
export function fixedBrowserBoundaryBundle(){
  const existing=fixedCandidateBundle();
  const lifecycle=decode(moduleUrl(existing.source,'observeCandidate'));
  const replacements={
    __ASSETS__:moduleUrl(lifecycle,'createCandidateAssets'),
    __SERVER__:moduleUrl(lifecycle,'openCandidateAssetServer'),
    __DRIVER__:moduleUrl(lifecycle,'createCandidateDriver'),
    __SECURITY__:moduleUrl(existing.source,'security'),
    __SANDBOX__:moduleUrl(existing.source,'runFixedCompatibility')
  };
  const file=new URL('./windows_pilot_browser_boundary_guest.mjs',import.meta.url);
  const stat=fs.lstatSync(file);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>16000)fail('BOUNDARY_BUNDLE_CHANGED');
  let source=fs.readFileSync(file,'utf8').replace(/\r\n/g,'\n');
  for(const [key,value] of Object.entries(replacements))source=replaceOnce(source,key,value);
  return {source,adapterSha256:sha(source),evaluatorSha256:existing.evaluatorSha256};
}
const HTML='<!doctype html><html lang="en"><meta charset="utf-8"><title>Trusted renderer boundary probe</title><output id="display">PENDING</output><script src="./probe.js" defer></script></html>';
// Fixed benign test targets only. An opaque fetch avoids treating an
// ordinary CORS denial as evidence that a network connection was denied.
// This is a sampled browser-layer check, not a proof of network isolation.
const JS=String.raw`(async()=>{
  const display=document.getElementById('display');
  const denied=async url=>{
    try{await fetch(url,{mode:'no-cors',cache:'no-store',signal:AbortSignal.timeout(2000)});return false;}
    catch(e){return e instanceof TypeError;}
  };
  try{
    const missing=await Promise.all(['/missing.js','/report.json','/etc/passwd','/index.html?extra=1','/%69ndex.html']
      .map(async url=>{const r=await fetch(url,{cache:'no-store'});return r.status===404&&(await r.text())==='';}));
    const post=await fetch('/index.html',{method:'POST'});
    const own=await fetch('/probe.js',{cache:'no-store'});
    const noNode=['require','process','Buffer','module'].every(key=>typeof globalThis[key]==='undefined');
    const outside=await denied('http://203.0.113.1/');
    const service=await denied('http://127.0.0.1:11434/');
    display.textContent=missing.every(Boolean)&&post.status===404&&own.ok&&noNode&&outside&&service?
      'BOUNDARY_CONTROLS_PASS':'BOUNDARY_CONTROLS_FAIL';
  }catch{display.textContent='BOUNDARY_CONTROLS_FAIL';}
})();`;
export function fixedBrowserBoundaryFixture(bundle,policySha256,attemptId){
  if(!attempt(attemptId)||!digest(policySha256))fail('BOUNDARY_IDENTITY_INVALID');
  const files=[['index.html',HTML],['probe.js',JS]].map(([path,content])=>({path,content,sha256:sha(content),utf8Bytes:Buffer.byteLength(content)}));
  const manifest=files.map(({content,...rest})=>rest);
  const sourceFingerprint=sha(manifest.map(f=>f.path+'\t'+f.sha256).join('\n'));
  const archiveSha256=sha(JSON.stringify({kind:'fixed-browser-boundary-control',files}));
  const candidateSha256=sha(JSON.stringify({archiveSha256,sourceFingerprint}));
  const binding={admissionSha256:sha('fixed-browser-boundary:'+candidateSha256),candidateSha256,
    archiveSha256,sourceFingerprint,files:manifest};
  const payloadJson=JSON.stringify({schemaVersion:1,...binding,files});
  createCandidateAssets(payloadJson,binding);
  const expected={attemptId,candidateSha256,archiveSha256,evaluatorSha256:bundle.evaluatorSha256,
    adapterSha256:bundle.adapterSha256,policySha256};
  return {expected,source:replaceOnce(bundle.source,'__CA_FIXED_CANDIDATE_INPUT__',
    Buffer.from(JSON.stringify({payloadJson,binding,expected})).toString('base64'))};
}
export function parseBrowserBoundaryReport(raw,expected){
  const keys=['attemptId','candidateSha256','archiveSha256','evaluatorSha256','adapterSha256','policySha256'];
  const cases=['guest_security','chromium_sandbox','renderer_controls','browser_closed','server_closed'];
  if(!exact(expected,keys)||!attempt(expected.attemptId)||!keys.slice(1).every(k=>digest(expected[k]))||
    typeof raw!=='string'||raw.length>8000)fail('BOUNDARY_REPORT_INVALID');
  let x;try{x=JSON.parse(raw);}catch{fail('BOUNDARY_REPORT_INVALID');}
  if(!exact(x,['schemaVersion',...keys,'checks'])||x.schemaVersion!==1||
    !keys.every(k=>x[k]===expected[k])||!exact(x.checks,cases)||!cases.every(k=>typeof x.checks[k]==='boolean'))
    fail('BOUNDARY_REPORT_INVALID');
  return Object.freeze({status:cases.every(k=>x.checks[k])?'FIXED_BROWSER_BOUNDARY_PASS':'VERIFICATION_BLOCKED',
    checks:Object.freeze({...x.checks}),executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
}
