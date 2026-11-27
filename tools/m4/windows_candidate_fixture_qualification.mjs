#!/usr/bin/env node
// Owner-local fixed candidate-shaped qualification only. No source/archive
// parameter, generated execution lane, policy promotion or authorization.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {readPinnedSources} from './windows_calculator_evaluator_qualification.mjs';
import {trustedDockerArgs} from './windows_aet8_trusted_qualification.mjs';
import {IMAGE_TAG,validateDockerContext,validatePinnedImage,shellDocker,writePrivateEvidence} from './windows_docker_qualification.mjs';
import {checkedChromiumSeccomp,FIXTURE_PID_LIMIT} from './windows_chromium_sandbox_qualification.mjs';
import {CALCULATION_CASES} from './calculation_journal_contract.mjs';
import {parseCandidateObservations} from './static_web_candidate_observations.mjs';
import {createCandidateAssets} from './static_web_candidate_assets.mjs';

const FIXTURES=['working','broken','spoof'];
const STAGES=['START','SECURITY','BUILD','SANDBOX','COMPLETE','DEADLINE','BLOCKED'];
const sha=x=>createHash('sha256').update(x).digest('hex');
const b64=x=>Buffer.from(x).toString('base64');
const fail=code=>{throw Error(code);};
const MODULES=[
  'windows_aet8_trusted_guest.mjs','windows_chromium_sandbox_guest.mjs',
  'windows_aet8_pipe_driver.mjs','calculation_journal_contract.mjs',
  'static_web_candidate_assets.mjs','static_web_candidate_server.mjs',
  'static_web_candidate_driver.mjs','static_web_candidate_observations.mjs',
  'static_web_candidate_lifecycle.mjs','windows_candidate_fixture_guest.mjs'
];
function read(name){
  if(!MODULES.includes(name))fail('FIXTURE_MODULE_NOT_REVIEWED');
  const url=new URL('./'+name,import.meta.url),stat=fs.lstatSync(url);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>100000)fail('FIXTURE_MODULE_INVALID');
  return fs.readFileSync(url,'utf8').replace(/\r\n/g,'\n');
}
function replaceOnce(text,from,to){
  if(text.split(from).length!==2)fail('FIXTURE_BUNDLE_CHANGED');
  return text.replace(from,()=>to);
}
// Static import closure only, with no guest filesystem writes or arbitrary
// resolution. Expose the already-reviewed fixed security probe in this
// embedded copy; the original qualification module bytes never change.
export function fixedCandidateBundle(){
  const sources=Object.fromEntries(MODULES.map(name=>[name,read(name)]));
  const hashes=Object.fromEntries(MODULES.map(name=>[name,sha(sources[name])]));
  const cache=new Map();
  function bundle(name,ancestors=[]){
    if(ancestors.includes(name)||!Object.hasOwn(sources,name))fail('FIXTURE_BUNDLE_CHANGED');
    if(cache.has(name))return cache.get(name);
    let code=sources[name];
    if(name==='windows_aet8_trusted_guest.mjs')
      code=replaceOnce(code,'async function security(){','export async function security(){');
    code=code.replace(/from '\.\/([a-z0-9_]+\.mjs)'/g,(_match,dep)=>
      "from 'data:text/javascript;base64,"+b64(bundle(dep,[...ancestors,name]))+"'");
    if(/from ['"]\.\.?\//.test(code))fail('FIXTURE_BUNDLE_CHANGED');
    cache.set(name,code);return code;
  }
  const source=bundle('windows_candidate_fixture_guest.mjs');
  return {source,hashes,adapterSha256:sha(source),evaluatorSha256:hashes['calculation_journal_contract.mjs']};
}
export function fixedCandidateFixture(name,bundle,policySha256,attemptId){
  if(!FIXTURES.includes(name)||!/^[a-f0-9]{32}$/.test(attemptId)||attemptId.length!==32)
    fail('FIXTURE_SELECTION_INVALID');
  const original=readPinnedSources();
  const working=original['working.html'].toString('utf8');
  const source=name==='working'?[['index.html',working],['working.js',original['working.js'].toString('utf8')]]:
    name==='broken'?[['index.html',original['broken.html'].toString('utf8')]]:
    [['index.html',replaceOnce(working,'./working.js','./spoof.js')],
      ['spoof.js','document.body.dataset.verified="true";window.verification={status:"VERIFIED",passed:7};document.querySelectorAll=()=>[];']];
  const files=source.map(([path,content])=>({path,sha256:sha(content),utf8Bytes:Buffer.byteLength(content),content}));
  files.sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0);
  const manifest=files.map(({content,...entry})=>entry);
  const sourceFingerprint=sha([...manifest].sort((a,b)=>a.path.toUpperCase()<b.path.toUpperCase()?-1:1)
    .map(f=>f.path+'\t'+f.sha256).join('\n'));
  // Qualification identities are explicitly synthetic, not a project ZIP
  // or approval. The same guest-shaped payload contract is exercised.
  const archiveSha256=sha(JSON.stringify({kind:'fixed-fixture-only',files}));
  const candidateSha256=sha(JSON.stringify({name,archiveSha256,sourceFingerprint}));
  const admissionSha256=sha('fixed-fixture-only:'+candidateSha256);
  const binding={admissionSha256,candidateSha256,archiveSha256,sourceFingerprint,files:manifest};
  const payloadJson=JSON.stringify({schemaVersion:1,...binding,files});
  createCandidateAssets(payloadJson,binding);
  const expected={attemptId,candidateSha256,archiveSha256,evaluatorSha256:bundle.evaluatorSha256,
    adapterSha256:bundle.adapterSha256,policySha256};
  const request={payloadJson,binding,expected};
  return {expected,source:replaceOnce(bundle.source,'__CA_FIXED_CANDIDATE_INPUT__',b64(JSON.stringify(request)))};
}
export function parseFixedCandidateStream(raw,expected){
  if(typeof raw!=='string'||raw.length>32000)fail('FIXTURE_REPORT_INVALID');
  let stage=null,observations=null;
  for(const line of raw.trim().split(/\r?\n/).filter(Boolean)){
    let parsed;try{parsed=JSON.parse(line);}catch{fail('FIXTURE_REPORT_INVALID');}
    if(observations)fail('FIXTURE_REPORT_INVALID');
    if(parsed?.kind==='progress'){
      if(Object.keys(parsed).length!==2||!STAGES.includes(parsed.stage))fail('FIXTURE_REPORT_INVALID');
      stage=parsed.stage;
    }else{
      if(stage!=='COMPLETE')fail('FIXTURE_REPORT_INVALID');
      observations=parseCandidateObservations(line,expected);
    }
  }
  return {stage,observations};
}
function fixedAdapterDockerArgs(imageId,name){
  const args=trustedDockerArgs(imageId,name);
  if(args.at(-1)!=='--run-trusted-aet8')fail('FIXTURE_POLICY_CHANGED');
  // Do not trigger the imported original trusted guest's CLI entrypoint.
  return [...args.slice(0,-1),'--run-fixed-candidate-fixture'];
}
export function qualifyFixedCandidate({platform=process.platform,env=process.env,docker=shellDocker,
  makeId=()=>randomBytes(16).toString('hex')}={}){
  const report={schemaVersion:1,stage:'AET-8-fixed-candidate-adapter',status:'BLOCKED',recordedAtUtc:new Date().toISOString(),
    imageId:null,registryDigest:null,seccompSha256:null,fixturePidLimit:FIXTURE_PID_LIMIT,
    moduleHashes:null,adapterSha256:null,evaluatorSha256:null,policySha256:null,
    fixtures:{},generatedSourceExecuted:false,generatedCodeExecutionEnabled:false,runtimeQualified:false,
    generatedArtifactVerified:false,reason:null};
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT){report.reason='LOCAL_WINDOWS_ONLY';return report;}
  const json=args=>{const x=docker(args,9000);if(x.status!==0||!x.stdout)fail('DOCKER_METADATA_UNAVAILABLE');try{return JSON.parse(x.stdout);}catch{fail('DOCKER_METADATA_INVALID');}};
  try{
    const bundle=fixedCandidateBundle();
    report.moduleHashes=bundle.hashes;report.adapterSha256=bundle.adapterSha256;report.evaluatorSha256=bundle.evaluatorSha256;
    for(const fixture of FIXTURES){
      const id=makeId();
      if(typeof id!=='string'||id.length!==32||!/^[a-f0-9]{32}$/.test(id))fail('FIXTURE_ID_INVALID');
      const name='ca-win-aet8-'+id.slice(0,12);
      const receipt={lastStage:null,observations:null,containerRemoved:false};
      report.fixtures[fixture]=receipt;
      const current=docker(['context','show'],8000);
      if(current.status!==0||!current.stdout)fail('DOCKER_CONTEXT_UNAVAILABLE');
      const context=current.stdout.trim();
      validateDockerContext(context,json(['context','inspect',context,'--format','{{json .}}'])?.Endpoints?.docker?.Host,
        json(['info','--format','{{json .}}']));
      const pin=validatePinnedImage(json(['image','inspect',IMAGE_TAG,'--format','{{json .}}']));
      const seccomp=checkedChromiumSeccomp();
      const policySha256=sha(JSON.stringify({kind:'fixed-fixture-only',imageId:pin.imageId,registryDigest:pin.registryDigest,
        seccompSha256:seccomp.sha256,fixturePidLimit:FIXTURE_PID_LIMIT,
        args:fixedAdapterDockerArgs(pin.imageId,'ca-win-aet8-000000000000').map(a=>a.startsWith('--security-opt=seccomp=')?'--security-opt=seccomp='+seccomp.sha256:a)}));
      if(report.policySha256!==null&&report.policySha256!==policySha256)fail('FIXTURE_POLICY_CHANGED');
      Object.assign(report,{imageId:pin.imageId,registryDigest:pin.registryDigest,seccompSha256:seccomp.sha256,policySha256});
      const payload=fixedCandidateFixture(fixture,bundle,policySha256,id);
      const absent=()=>{const x=docker(['ps','-a','--filter','name=^/'+name+'$','--format','{{.ID}}'],15000);return x.status===0&&x.stdout.trim()==='';};
      if(!absent())fail('DISPOSABLE_NAME_COLLISION');
      try{
        // Same approved trusted fixture Docker policy. The new guest has no
        // command-line dispatch; its input is the frozen embedded fixture.
        const execution=docker(fixedAdapterDockerArgs(pin.imageId,name),180000,payload.source);
        const parsed=parseFixedCandidateStream(execution.stdout||'',payload.expected);
        receipt.lastStage=parsed.stage;receipt.observations=parsed.observations;
        if(execution.timedOut)fail('FIXTURE_DEADLINE_EXCEEDED');
        const observations=parsed.observations;
        if(execution.status!==0||!observations||observations.completed!==CALCULATION_CASES.length||
          observations.status!==(fixture==='working'?'CHECKS_PASSED_NOT_VERIFIED':'TESTS_FAILED')||
          observations.passed!==(fixture==='working'?CALCULATION_CASES.length:0))
          fail('FIXTURE_EXPECTATIONS_NOT_MET');
      }finally{
        try{
          const found=docker(['container','inspect',name,'--format','{{json .Config.Labels}}'],15000);
          if(found.status===0){
            if(JSON.parse(found.stdout)?.['codingassistant.aet8']!==name)fail('CONTAINER_OWNERSHIP_MISMATCH');
            docker(['rm','-f',name],15000);
          }
          receipt.containerRemoved=absent();
        }catch{receipt.containerRemoved=false;}
        if(!receipt.containerRemoved)fail('CONTAINER_CLEANUP_UNCONFIRMED');
      }
    }
    report.status='FIXED_CANDIDATE_ADAPTER_PASS';
  }catch(e){report.reason=/^[A-Z_]{3,80}$/.test(e?.message??'')?e.message:'FIXTURE_QUALIFICATION_BLOCKED';}
  return report;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('AET-8 FIXED_CANDIDATE_ADAPTER: NOT_RUN');return 0;}
  if(argv.length!==1||argv[0]!=='--run'){console.error('Only --run for frozen trusted fixtures is supported');return 2;}
  const report=qualifyFixedCandidate();
  console.log('AET-8 WINDOWS_'+report.status);
  for(const [name,value] of Object.entries(report.fixtures)){
    const o=value.observations;
    console.log('  '+name+': '+(o?o.passed+'/'+o.required+' ('+o.completed+' completed, '+o.status+')':'NOT_COMPLETE')+
      '; stage: '+value.lastStage+'; container removed: '+value.containerRemoved);
    if(o)for(const key of CALCULATION_CASES)if(o.cases[key]!== (name==='working'))console.log('    unexpected: '+key+' = '+o.cases[key]);
  }
  if(report.reason)console.log('  blocker: '+report.reason);
  console.log('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED');
  console.log('Evidence: '+writePrivateEvidence(report));
  return report.status==='FIXED_CANDIDATE_ADAPTER_PASS'?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
