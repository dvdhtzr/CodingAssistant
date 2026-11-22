#!/usr/bin/env node
// M4.4 — Windows-only trusted Calculator source-snapshot acceptance.
// Never accepts user/model/project paths; not an untrusted evaluator.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {createHash,randomBytes} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {CASES,FILES} from './windows_calculator_evaluator_guest.mjs';
import {
  IMAGE_TAG,validateDockerContext,validatePinnedImage,
  shellDocker,writePrivateEvidence,classifyGuestFailure,
} from './windows_docker_qualification.mjs';

const BASE=fileURLToPath(new URL(
  '../../src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/',
  import.meta.url));
const EXPECTED=Object.freeze({
  'broken.html':'b02c79570ce9d45bc1cbe7c3ab40cd4bb083e237',
  'broken.js':'e5538c992feb36a5137b14837ffde2db3683455a',
  'working.html':'4afe63490ef18aef0ae1e71cf85760e6d84473da',
  'working.js':'ad7b3801d434d81065bbdbe661419fb859cf82c7',
});
const GUEST=fs.readFileSync(new URL('./windows_calculator_evaluator_guest.mjs',
  import.meta.url),'utf8');
const HARNESS=fs.readFileSync(new URL('./windows_calculator_harness.js',
  import.meta.url),'utf8');
const PLACEHOLDER='const HARNESS_SOURCE="PLACEHOLDER_M44_HARNESS_SOURCE";';
const SOURCE=GUEST.replace(PLACEHOLDER,
  'const HARNESS_SOURCE='+JSON.stringify(HARNESS)+';');
const NAME=/^ca-win-m44-[0-9a-f]{12}$/;
const ID=/^sha256:[0-9a-f]{64}$/;
const SENTINEL='CA_M44_UNMOUNTED_NEIGHBOR\n';
function fail(message){throw Error(message);}
function hash(bytes){return createHash('sha256').update(bytes).digest('hex');}
export function gitBlobHash(bytes){
  return createHash('sha1').update('blob '+bytes.length+'\0').update(bytes).digest('hex');
}
function unchangedFile(location,expected){
  try{
    const stat=fs.lstatSync(location);
    if(!stat.isFile()||stat.isSymbolicLink()||stat.size>50000)
      return false;
    return gitBlobHash(fs.readFileSync(location))===expected;
  }catch{return false;}
}
export function readPinnedSources(){
  if(FILES.length!==4 || Object.keys(EXPECTED).length!==4 ||
     GUEST.split(PLACEHOLDER).length!==2)
    fail('Evaluator source manifest invalid');
  const sources={};
  for(const name of FILES){
    const original=path.join(BASE,name);
    if(!unchangedFile(original,EXPECTED[name]))
      fail('Reviewed fixture hash changed: '+name);
    sources[name]=fs.readFileSync(original);
  }
  return sources;
}
export function prepareSnapshot(root,sources){
  const snapshot=path.join(root,'snapshot'),neighbor=path.join(root,'neighbor');
  fs.mkdirSync(snapshot,{mode:0o700});
  fs.mkdirSync(neighbor,{mode:0o700});
  for(const name of FILES){
    if(!Buffer.isBuffer(sources[name]) ||
       gitBlobHash(sources[name])!==EXPECTED[name])
      fail('Unapproved source bytes');
    fs.writeFileSync(path.join(snapshot,name),sources[name],{flag:'wx',mode:0o600});
  }
  fs.writeFileSync(path.join(neighbor,'keep.txt'),SENTINEL,{flag:'wx'});
  return {root,snapshot,neighbor};
}
export function verifySnapshot(snapshot){
  const exact=fs.realpathSync(snapshot);
  if(fs.lstatSync(snapshot).isSymbolicLink() ||
     path.basename(exact)!=='snapshot' ||
     path.dirname(path.dirname(exact))!==fs.realpathSync(os.tmpdir()) ||
     !/^ca-m44-[A-Za-z0-9_-]{6,}$/.test(path.basename(path.dirname(exact))) ||
     exact.includes(',') || /[\r\n]/.test(exact) ||
     fs.readdirSync(snapshot).sort().join('|')!==[...FILES].sort().join('|'))
    fail('Only an exact disposable, fixed-source snapshot may be mounted');
  for(const name of FILES)
    if(!unchangedFile(path.join(snapshot,name),EXPECTED[name]))
      fail('Snapshot contains altered source or symlink');
  return exact;
}
export function dockerArgs(imageId,name,snapshot){
  if(!ID.test(imageId)||!NAME.test(name))fail('Invalid image or disposable guest');
  const scoped=verifySnapshot(snapshot);
  return [
    'run','--rm','-i','--pull=never','--name',name,
    '--label','codingassistant.m44='+name,
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2',
    '--ipc=private','--shm-size=256m',
    '--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m',
    '--mount','type=bind,source='+scoped+
      ',target=/workspace,readonly,bind-propagation=rprivate',
    '--workdir=/workspace',
    '--env=HOME=/tmp','--env=XDG_CONFIG_HOME=/tmp',
    '--env=XDG_CACHE_HOME=/tmp',
    '--entrypoint=node',imageId,
    '--input-type=module','-','--run-trusted-evaluator',
  ];
}
export function parseEvaluation(raw){
  if(typeof raw!=='string'||raw.length<2||raw.length>4000)
    fail('Evaluator result absent or oversized');
  let value;
  try{value=JSON.parse(raw.trim());}catch{fail('Evaluator returned malformed JSON');}
  if(!value||Array.isArray(value)||typeof value!=='object'||
     Object.keys(value).length!==CASES.length ||
     !CASES.every(k=>Object.hasOwn(value,k)&&typeof value[k]==='boolean'))
    fail('Evaluator did not report exact Boolean case schema');
  return Object.fromEntries(CASES.map(key=>[key,value[key]]));
}
export function auditSnapshot(fixture){
  const entries=fs.readdirSync(fixture.snapshot).sort();
  const pinned=entries.length===FILES.length &&
    entries.join('|')===[...FILES].sort().join('|') &&
    FILES.every(name=>unchangedFile(path.join(fixture.snapshot,name),EXPECTED[name]));
  return {
    exact_snapshot_unchanged:pinned,
    unmounted_neighbor_unchanged:
      fs.readFileSync(path.join(fixture.neighbor,'keep.txt'),'utf8')===SENTINEL,
    source_repo_untouched:FILES.every(name=>
      unchangedFile(path.join(BASE,name),EXPECTED[name])),
    no_snapshot_git_metadata:!fs.existsSync(path.join(fixture.snapshot,'.git')),
  };
}
function json(docker,args,label){
  const r=docker(args,9000);
  if(r.status!==0||!r.stdout)fail(label+' unavailable');
  try{return JSON.parse(r.stdout.trim());}catch{fail(label+' invalid JSON');}
}
export function runQualification({
  platform=process.platform,env=process.env,docker=shellDocker,
  name='ca-win-m44-'+randomBytes(6).toString('hex'),
}={}){
  const report={
    schemaVersion:1,stage:'M4.4-windows-trusted-calculator-source-verification',
    status:'BLOCKED',recordedAtUtc:new Date().toISOString(),
    imageTag:IMAGE_TAG,imageId:null,registryDigest:null,
    guestSha256:hash(GUEST),harnessSha256:hash(HARNESS),
    trustedSources:null,tests:null,passedTests:0,requiredTests:CASES.length,
    audit:null,hostPassed:0,requiredHost:4,
    containerRemoved:false,temporarySnapshotRemoved:false,
    modelInvoked:false,generatedSourceExecuted:false,
    untrustedBrowserQualified:false,staticWebRuntimeQualified:false,
    generatedCodeExecutionEnabled:false,verificationStatus:'GENERATED_NOT_TESTED',
    reason:null,
  };
  if(platform!=='win32'||!NAME.test(name)||env.DOCKER_HOST||env.DOCKER_CONTEXT){
    report.reason='Windows local Docker Desktop only; no context overrides';
    return report;
  }
  let fixture=null,attempted=false;
  try{
    const sources=readPinnedSources();
    report.trustedSources=Object.fromEntries(FILES.map(n=>[n,hash(sources[n])]));
    const current=docker(['context','show'],8000);
    if(current.status!==0||!current.stdout)fail('Docker context unavailable');
    const context=current.stdout.trim();
    const endpoint=json(docker,['context','inspect',context,'--format','{{json .}}'],
      'Docker context');
    validateDockerContext(context,endpoint?.Endpoints?.docker?.Host,
      json(docker,['info','--format','{{json .}}'],'Docker info'));
    const pinned=validatePinnedImage(json(docker,[
      'image','inspect',IMAGE_TAG,'--format','{{json .}}'],'Playwright image'));
    report.imageId=pinned.imageId;
    report.registryDigest=pinned.registryDigest;
    const collision=docker(['ps','-a','--filter','name=^/'+name+'$',
      '--format','{{.ID}}'],9000);
    if(collision.status!==0||collision.stdout.trim())
      fail('Existing guest name or Docker preflight failure');
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m44-'));
    fixture={root};
    Object.assign(fixture,prepareSnapshot(root,sources));
    attempted=true;
    const result=docker(dockerArgs(pinned.imageId,name,fixture.snapshot),
      135000,SOURCE);
    if(result.timedOut)fail('Evaluator guest timed out');
    if(!result.stdout||!result.stdout.trim())
      fail('No evaluator observations; Docker exit='+
        (Number.isInteger(result.status)?result.status:'unknown')+
        '; '+classifyGuestFailure(result));
    const observations=parseEvaluation(result.stdout);
    report.tests=observations;
    report.passedTests=CASES.filter(k=>observations[k]).length;
    const audit=auditSnapshot(fixture);
    report.audit=audit;
    report.hostPassed=Object.values(audit).filter(Boolean).length;
    if(result.status!==0||report.passedTests!==CASES.length)
      fail('Independent reference evaluation failed: '+
        CASES.filter(k=>!observations[k]).join(', '));
    if(report.hostPassed!==report.requiredHost)
      fail('Host source integrity check failed: '+
        Object.keys(audit).filter(k=>!audit[k]).join(', '));
    report.status='TRUSTED_REFERENCE_EVALUATOR_PASS';
    report.verificationStatus='TRUSTED_FIXTURE_ONLY';
  }catch(error){report.reason=String(error?.message??error).slice(0,260);}
  finally{
    if(attempted){
      try{
        const found=docker(['container','inspect',name,
          '--format','{{json .Config.Labels}}'],15000);
        if(found.status===0){
          const labels=JSON.parse(found.stdout.trim());
          if(labels?.['codingassistant.m44']!==name)
            fail('Container ownership could not be verified');
          docker(['rm','-f',name],15000);
        }
        const left=docker(['ps','-a','--filter','name=^/'+name+'$',
          '--format','{{.ID}}'],15000);
        report.containerRemoved=left.status===0 && left.stdout.trim()==='';
      }catch{report.containerRemoved=false;}
    }
    if(fixture){
      try{
        if(path.dirname(fixture.root)!==fs.realpathSync(os.tmpdir())||
          !/^ca-m44-[A-Za-z0-9_-]{6,}$/.test(path.basename(fixture.root)))
          fail('Unsafe temporary cleanup root');
        fs.rmSync(fixture.root,{recursive:true,force:true,maxRetries:3});
        report.temporarySnapshotRemoved=!fs.existsSync(fixture.root);
      }catch{report.temporarySnapshotRemoved=false;}
    }
    if(attempted&&(!report.containerRemoved||!report.temporarySnapshotRemoved)){
      report.status='BLOCKED';report.verificationStatus='GENERATED_NOT_TESTED';
      report.reason='Container or trusted snapshot cleanup not confirmed';
    }
  }
  return report;
}
export function main(argv=process.argv.slice(2),options={}){
  if(argv.length===0){
    process.stdout.write('M4.4 WINDOWS_REFERENCE_EVALUATOR: NOT_RUN\n');
    process.stdout.write('Use --run only for fixed trusted source snapshots.\n');
    return 0;
  }
  if(argv.length!==1||argv[0]!=='--run'){
    process.stderr.write('Usage: node tools/m4/windows_calculator_evaluator_qualification.mjs [--run]\n');
    return 2;
  }
  const report=runQualification(options);
  const evidence=writePrivateEvidence(report,options.evidenceFolder);
  process.stdout.write('M4.4 WINDOWS_'+report.status+'\n');
  process.stdout.write('  independent evaluator cases: '+
    report.passedTests+'/'+report.requiredTests+'\n');
  process.stdout.write('  host snapshot integrity: '+
    report.hostPassed+'/'+report.requiredHost+'\n');
  process.stdout.write('  container removed: '+report.containerRemoved+
    '; snapshot removed: '+report.temporarySnapshotRemoved+'\n');
  process.stdout.write('  generated source / untrusted browser: NOT_AUTHORIZED\n');
  process.stdout.write('  Static Web runtime: UNQUALIFIED\n');
  if(report.reason)process.stdout.write('  blocker: '+report.reason+'\n');
  process.stdout.write('Evidence: '+evidence+'\n');
  return report.status==='TRUSTED_REFERENCE_EVALUATOR_PASS' &&
    report.containerRemoved&&report.temporarySnapshotRemoved?0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=main();
