#!/usr/bin/env node
// M4.3 Windows Docker Desktop: ONLY a freshly created, trusted miniature
// project may be mounted. No model, user source, repo, secrets or socket.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {randomBytes,createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {
  IMAGE_TAG,validateDockerContext,validatePinnedImage,
  shellDocker,writePrivateEvidence,classifyGuestFailure,
} from './windows_docker_qualification.mjs';
import {FIXTURE,CASES} from './windows_scoped_workspace_guest.mjs';

const SOURCE=fs.readFileSync(new URL('./windows_scoped_workspace_guest.mjs',import.meta.url),'utf8');
const NAME=/^ca-win-m43-[a-f0-9]{12}$/;
const ID=/^sha256:[a-f0-9]{64}$/;
const CHILD_FILES=['app.js','child.txt','escape','index.html','notes.txt','seed.txt'];

function fail(message){throw Error(message);}
function hash(v){return createHash('sha256').update(v).digest('hex');}
function read(p){try{return fs.readFileSync(p,'utf8');}catch{return null;}}
function info(docker,args,label){
  const r=docker(args,9000);
  if(r.status!==0||!r.stdout)fail(label+' unavailable');
  try{return JSON.parse(r.stdout.trim());}catch{fail(label+' invalid JSON');}
}

export function setupFixture(root){
  // Caller must have just created root under OS-owned temporary directory.
  const approved=path.join(root,'approved');
  const outside=path.join(root,'outside');
  fs.mkdirSync(approved,{mode:0o700});
  fs.mkdirSync(outside,{mode:0o700});
  fs.writeFileSync(path.join(approved,'seed.txt'),FIXTURE.seedBefore,{flag:'wx'});
  fs.writeFileSync(path.join(approved,'notes.txt'),FIXTURE.notes,{flag:'wx'});
  fs.writeFileSync(path.join(outside,'keep.txt'),FIXTURE.outside,{flag:'wx'});
  return {approved,outside,root,sentinelHash:hash(FIXTURE.outside)};
}

export function verifyFixturePath(approved){
  const target=fs.realpathSync(approved);
  const parent=path.dirname(target);
  const prefix=fs.realpathSync(os.tmpdir());
  if(path.dirname(parent)!==prefix ||
     !/^ca-m43-[a-zA-Z0-9_-]{6,}$/.test(path.basename(parent)) ||
     path.basename(target)!=='approved' ||
     !fs.statSync(target).isDirectory() ||
     fs.lstatSync(approved).isSymbolicLink() ||
     target.includes(',') || /[\r\n]/.test(target) ||
     fs.readdirSync(approved).sort().join('|')!=='notes.txt|seed.txt' ||
     fs.readdirSync(approved).some(name=>fs.lstatSync(path.join(approved,name)).isSymbolicLink()))
    fail('Only a newly generated, direct temp approved fixture may be mounted');
  return target;
}

export function workspaceDockerArgs(imageId,name,approved){
  if(!ID.test(imageId)||!NAME.test(name))fail('Invalid immutable image or container name');
  const source=verifyFixturePath(approved);
  return [
    'run','--rm','-i','--pull=never','--name',name,
    '--label','codingassistant.m43='+name,
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2',
    '--ipc=private','--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m',
    '--shm-size=256m','--init',
    '--mount','type=bind,source='+source+',target=/workspace,bind-propagation=rprivate',
    '--workdir=/workspace','--env=HOME=/tmp',
    '--env=XDG_CONFIG_HOME=/tmp/config',
    '--env=XDG_CACHE_HOME=/tmp/cache',
    '--entrypoint=node',imageId,
    '--input-type=module','-','--run-fixed-workspace',
  ];
}

export function parseWorkspaceResults(raw){
  if(typeof raw!=='string'||raw.length<2||raw.length>4000)
    fail('Guest workspace observations absent or oversized');
  let v;try{v=JSON.parse(raw.trim());}catch{fail('Guest workspace JSON invalid');}
  if(!v||Array.isArray(v)||typeof v!=='object'||
     Object.keys(v).length!==CASES.length ||
     !CASES.every(k=>Object.hasOwn(v,k)&&typeof v[k]==='boolean'))
    fail('Guest workspace observations schema invalid');
  return Object.fromEntries(CASES.map(k=>[k,v[k]]));
}

export function hostAudit(fixture){
  const {approved,outside,sentinelHash}=fixture;
  const names=fs.readdirSync(approved).sort();
  function exactSymlink(){
    try{
      const l=fs.lstatSync(path.join(approved,'escape'));
      return l.isSymbolicLink() &&
        fs.readlinkSync(path.join(approved,'escape')).replaceAll('\\','/')==='/etc/passwd';
    }catch{return false;}
  }
  function regular(file){
    try{return fs.lstatSync(path.join(approved,file)).isFile();}catch{return false;}
  }
  const expected=CHILD_FILES.join('|');
  return {
    exact_file_allowlist:names.join('|')===expected,
    approved_seed_changed:regular('seed.txt') && read(path.join(approved,'seed.txt'))===FIXTURE.seedAfter,
    generated_html_matches:regular('index.html') && read(path.join(approved,'index.html'))===FIXTURE.html,
    generated_js_matches:regular('app.js') && read(path.join(approved,'app.js'))===FIXTURE.js,
    child_file_matches:regular('child.txt') && read(path.join(approved,'child.txt'))===FIXTURE.child,
    unchanged_notes:regular('notes.txt') && read(path.join(approved,'notes.txt'))===FIXTURE.notes,
    outside_sentinel_unchanged:read(path.join(outside,'keep.txt'))===FIXTURE.outside &&
      hash(read(path.join(outside,'keep.txt'))??'')===sentinelHash,
    symlink_target_unmodified:exactSymlink(),
    no_git_metadata:!fs.existsSync(path.join(approved,'.git')),
  };
}

export function runWorkspace({
  platform=process.platform, env=process.env, docker=shellDocker,
  name='ca-win-m43-'+randomBytes(6).toString('hex'),
}={}){
  const report={
    schemaVersion:1,stage:'M4.3-windows-scoped-disposable-workspace',
    recordedAtUtc:new Date().toISOString(),status:'BLOCKED',
    imageTag:IMAGE_TAG,imageId:null,registryDigest:null,
    guestSourceSha256:hash(SOURCE),cases:null,passedCases:0,
    requiredCases:CASES.length,hostAudit:null,hostPassed:0,requiredHost:9,
    guestRemoved:false,temporaryFixtureRemoved:false,
    userCodeExecuted:false,modelInvoked:false,originalRepoMounted:false,
    hostSecretsMounted:false,untrustedBrowserQualified:false,
    executableRuntimeQualified:false,generatedCodeExecutionEnabled:false,
    reason:null,
  };
  if(platform!=='win32'||!NAME.test(name) || env.DOCKER_HOST||env.DOCKER_CONTEXT){
    report.reason='Windows local Docker Desktop only, no Docker context override';
    return report;
  }
  let attempted=false,fixture=null;
  try{
    const contextResult=docker(['context','show'],8000);
    if(contextResult.status!==0||!contextResult.stdout)fail('Docker context unavailable');
    const context=contextResult.stdout.trim();
    const endpoint=info(docker,['context','inspect',context,'--format','{{json .}}'],'Docker context');
    const daemon=info(docker,['info','--format','{{json .}}'],'Docker info');
    validateDockerContext(context,endpoint?.Endpoints?.docker?.Host,daemon);
    const pin=validatePinnedImage(info(docker,
      ['image','inspect',IMAGE_TAG,'--format','{{json .}}'],'Cached image'));
    report.imageId=pin.imageId;
    report.registryDigest=pin.registryDigest;
    const collision=docker(['ps','-a','--filter','name=^/'+name+'$',
      '--format','{{.ID}}'],8000);
    if(collision.status!==0||collision.stdout.trim())
      fail('Disposable name collision or Docker preflight failed');
    // OS-managed TEMP is not the user's repository, home mount or project.
    const root=fs.mkdtempSync(path.join(os.tmpdir(),'ca-m43-'));
    fixture=setupFixture(root);
    const args=workspaceDockerArgs(pin.imageId,name,fixture.approved);
    attempted=true;
    const guest=docker(args,110000,SOURCE);
    if(guest.timedOut)fail('Fixed workspace guest timed out');
    if(!guest.stdout||!guest.stdout.trim())
      fail('Workspace guest produced no report; exit='+
        (Number.isInteger(guest.status)?guest.status:'unknown')+
        '; category='+classifyGuestFailure(guest));
    const cases=parseWorkspaceResults(guest.stdout);
    report.cases=cases;
    report.passedCases=CASES.filter(k=>cases[k]).length;
    const audit=hostAudit(fixture);
    report.hostAudit=audit;
    report.hostPassed=Object.values(audit).filter(Boolean).length;
    if(guest.status!==0||report.passedCases!==CASES.length)
      fail('Workspace guest blocked: ' + CASES.filter(k=>!cases[k]).join(', '));
    if(report.hostPassed!==report.requiredHost)
      fail('Independent host audit blocked: '+
        Object.keys(audit).filter(k=>!audit[k]).join(', '));
    report.status='SCOPED_FIXTURE_PASS';
  }catch(e){report.reason=String(e?.message??e).slice(0,260);}
  finally{
    if(attempted){
      try{
        const owner=docker(['container','inspect',name,
          '--format','{{json .Config.Labels}}'],15000);
        if(owner.status===0){
          const labels=JSON.parse(owner.stdout.trim());
          if(labels?.['codingassistant.m43']!==name)
            fail('Container ownership label mismatch');
          docker(['rm','-f',name],15000);
        }
        const left=docker(['ps','-a','--filter','name=^/'+name+'$',
          '--format','{{.ID}}'],15000);
        report.guestRemoved=left.status===0 && left.stdout.trim()==='';
      }catch{report.guestRemoved=false;}
    }
    if(fixture){
      try{
        // Only delete the exact randomly created temporary root.
        const base=fs.realpathSync(os.tmpdir());
        if(path.dirname(fixture.root)!==base ||
           !/^ca-m43-[a-zA-Z0-9_-]{6,}$/.test(path.basename(fixture.root)))
          fail('Unexpected temporary fixture cleanup path');
        fs.rmSync(fixture.root,{recursive:true,force:true,maxRetries:3});
        report.temporaryFixtureRemoved=!fs.existsSync(fixture.root);
      }catch{report.temporaryFixtureRemoved=false;}
    }
    if(attempted && (!report.guestRemoved||!report.temporaryFixtureRemoved)){
      report.status='BLOCKED';
      report.reason='Container or disposable host fixture cleanup not confirmed';
    }
  }
  return report;
}

export function main(argv=process.argv.slice(2),options={}){
  if(argv.length===0){
    process.stdout.write('M4.3 WINDOWS_SCOPED_WORKSPACE: NOT_RUN\n');
    process.stdout.write('Use --run only for trusted disposable fixture qualification.\n');
    return 0;
  }
  if(argv.length!==1||argv[0]!=='--run'){
    process.stderr.write('Usage: node tools/m4/windows_scoped_workspace_qualification.mjs [--run]\n');
    return 2;
  }
  const report=runWorkspace(options);
  const evidence=writePrivateEvidence(report,options.evidenceFolder);
  process.stdout.write('M4.3 WINDOWS_'+report.status+'\n');
  process.stdout.write('  guest cases: '+report.passedCases+'/'+report.requiredCases+'\n');
  process.stdout.write('  independent host checks: '+report.hostPassed+'/'+report.requiredHost+'\n');
  process.stdout.write('  container absent: '+report.guestRemoved+
    '; fixture removed: '+report.temporaryFixtureRemoved+'\n');
  process.stdout.write('  generated source / original repo: NOT_AUTHORIZED\n');
  process.stdout.write('  Static Web runtime: UNQUALIFIED\n');
  if(report.reason)process.stdout.write('  blocker: '+report.reason+'\n');
  process.stdout.write('Evidence: '+evidence+'\n');
  return report.status==='SCOPED_FIXTURE_PASS' && report.guestRemoved &&
    report.temporaryFixtureRemoved ? 0:2;
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=main();
