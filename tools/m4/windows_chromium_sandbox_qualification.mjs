#!/usr/bin/env node
// M4.7 Windows-only Chromium sandbox-ON COMPATIBILITY qualification.
// FIXED reviewed source only: does not authorize model-generated JavaScript.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash,randomBytes} from 'node:crypto';
import {
  IMAGE_TAG,validateDockerContext,validatePinnedImage,
  shellDocker,writePrivateEvidence,classifyGuestFailure,
} from './windows_docker_qualification.mjs';
import {
  CASES,BROWSER_CODES,BROWSER_KINDS,BROWSER_ARGS,CRASH_DETAILS,NAMESPACE_CODES,
} from './windows_chromium_sandbox_guest.mjs';

const SOURCE=fs.readFileSync(
  new URL('./windows_chromium_sandbox_guest.mjs',import.meta.url),'utf8');
const NAME=/^ca-win-m47-[0-9a-f]{12}$/;
const IMAGE_ID=/^sha256:[0-9a-f]{64}$/;
// Vendored unchanged from microsoft/playwright/utils/docker/seccomp_profile.json
// at upstream Git blob fddc05fb520affb145404e6f6f647ca96af8087d.
export const OFFICIAL_SECCOMP_GIT_BLOB='fddc05fb520affb145404e6f6f647ca96af8087d';
const SECCOMP_LOCATION=fileURLToPath(new URL(
  './profiles/playwright-chromium-seccomp.json',import.meta.url));
const FIXTURE_SECCOMP_LOCATION=fileURLToPath(new URL(
  './profiles/m47-fixed-chromium-chroot-seccomp.json',import.meta.url));
export const FIXTURE_SECCOMP_VARIANT='m47-fixed-chromium-chroot-v1';
const FIXTURE_RULE=Object.freeze({names:['chroot'],action:'SCMP_ACT_ALLOW',args:[],
  comment:'Owner-approved M4.7 fixed fixture only: permit Chromium namespace chroot; no capability grant',includes:{},excludes:{}});
function fail(message){throw Error(message);}
export function validateChromiumSeccompBytes(bytes){
  if(!Buffer.isBuffer(bytes)||bytes.length<1000||bytes.length>18000)
    fail('Chromium seccomp profile bytes outside approved bounds');
  const canonical=Buffer.from(bytes.toString('utf8').replace(/\r\n/g,'\n'));
  const blob=createHash('sha1').update(
    'blob '+canonical.length+'\0').update(canonical).digest('hex');
  if(blob!==OFFICIAL_SECCOMP_GIT_BLOB)
    fail('Chromium seccomp profile differs from approved upstream bytes');
  const policy=JSON.parse(canonical.toString('utf8'));
  if(policy.defaultAction!=='SCMP_ACT_ERRNO'||
     !Array.isArray(policy.syscalls)||
     !policy.syscalls.some(rule=>rule.action==='SCMP_ACT_ALLOW' &&
       rule.comment==='Allow create user namespaces' &&
       JSON.stringify(rule.names)==='["clone","setns","unshare"]') ||
     policy.syscalls.some(rule=>rule.action!=='SCMP_ACT_ALLOW'))
    fail('Chromium seccomp policy invalid');
  return {sha256:createHash('sha256').update(canonical).digest('hex'),
    upstreamGitBlob:blob};
}
// Pin the derived profile by exact canonical bytes reconstructed from the
// independently byte-pinned upstream original and ONE constant approved rule.
export function validateFixtureSeccompBytes(original,derived){
  const upstream=validateChromiumSeccompBytes(original);
  if(!Buffer.isBuffer(derived)||derived.length<1000||derived.length>24000)
    fail('Fixed fixture seccomp bytes outside approved bounds');
  const policy=JSON.parse(original.toString('utf8'));
  policy.syscalls.push(FIXTURE_RULE);
  const expected=Buffer.from(JSON.stringify(policy,null,2)+'\n');
  const canonical=Buffer.from(derived.toString('utf8').replace(/\r\n/g,'\n'));
  if(!expected.equals(canonical))
    fail('Fixed fixture policy differs from approved single chroot adjustment');
  return {sha256:createHash('sha256').update(canonical).digest('hex'),
    upstreamGitBlob:upstream.upstreamGitBlob,upstreamSha256:upstream.sha256,
    variant:FIXTURE_SECCOMP_VARIANT};
}
export function checkedChromiumSeccomp(){
  const stat=fs.lstatSync(SECCOMP_LOCATION);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>18000)
    fail('Official seccomp profile absent or not a regular file');
  const derivedStat=fs.lstatSync(FIXTURE_SECCOMP_LOCATION);
  if(!derivedStat.isFile()||derivedStat.isSymbolicLink()||derivedStat.size>24000)
    fail('Fixed fixture seccomp profile absent or not a regular file');
  const verified=validateFixtureSeccompBytes(fs.readFileSync(SECCOMP_LOCATION),
    fs.readFileSync(FIXTURE_SECCOMP_LOCATION));
  return {...verified,path:fs.realpathSync(FIXTURE_SECCOMP_LOCATION)};
}
function json(docker,args,what){
  const r=docker(args,8000);
  if(r.status!==0||typeof r.stdout!=='string'||!r.stdout.trim())
    fail(what+' unavailable');
  try{return JSON.parse(r.stdout.trim());}
  catch{fail(what+' malformed');}
}
// Use owner-approved fixture-only derivative of the pinned default-deny
// Playwright profile; keep all Docker
// restrictions and do NOT turn off the Chromium renderer sandbox.
export function sandboxDockerArgs(id,name){
  if(!IMAGE_ID.test(id)||!NAME.test(name))
    fail('Invalid pinned image or disposable guest name');
  if(BROWSER_ARGS.some(arg=>
    /(?:no-sandbox|disable-setuid-sandbox|single-process|no-zygote|disable-seccomp-filter-sandbox)/.test(arg)))
    fail('Browser sandbox bypass is forbidden');
  const seccomp=checkedChromiumSeccomp();
  return [
    'run','--rm','-i','--pull=never','--name',name,
    '--label','codingassistant.m47='+name,
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges',
    '--security-opt=seccomp='+seccomp.path,'--user=1000:1000',
    '--pids-limit=96','--memory=2g','--cpus=2',
    '--ipc=private','--shm-size=256m',
    '--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m',
    '--env=HOME=/tmp','--env=XDG_CONFIG_HOME=/tmp/config',
    '--env=XDG_CACHE_HOME=/tmp/cache',
    '--entrypoint=node',id,
    '--input-type=module','-','--run-fixed-sandbox-probe',
  ];
}
export function parseSandboxReport(raw){
  if(typeof raw!=='string'||raw.length<2||raw.length>6000)
    fail('Guest observations absent or oversized');
  let parsed;
  try{parsed=JSON.parse(raw.trim());}
  catch{fail('Guest report not valid JSON');}
  if(!parsed||Array.isArray(parsed)||typeof parsed!=='object' ||
     Object.keys(parsed).length!==3 || parsed.schemaVersion!==1 ||
     !Object.hasOwn(parsed,'cases')||!Object.hasOwn(parsed,'browser'))
    fail('Invalid sandbox compatibility envelope');
  const checks=parsed.cases,b=parsed.browser;
  if(!checks||Array.isArray(checks)||typeof checks!=='object' ||
     Object.keys(checks).length!==CASES.length ||
     !CASES.every(name=>Object.hasOwn(checks,name) &&
       typeof checks[name]==='boolean'))
    fail('Guest must report every exact Boolean case');
  if(!b||Array.isArray(b)||typeof b!=='object'||
     Object.keys(b).length!==6 ||
     !BROWSER_KINDS.includes(b.binary) ||
     !BROWSER_CODES.includes(b.dom) ||
     !BROWSER_CODES.includes(b.sandbox) ||
     !CRASH_DETAILS.includes(b.domDetail) || !CRASH_DETAILS.includes(b.sandboxDetail) ||
     !NAMESPACE_CODES.includes(b.namespaceProbe))
    fail('Unrecognized browser diagnostic categories');
  if(checks.sandboxed_chromium_fixed_dom && b.dom!=='PASS' ||
     checks.chromium_sandbox_status_adequate && (b.sandbox!=='PASS'||b.binary!=='FULL_CHROMIUM'))
    fail('Positive browser observations require matching browser evidence');
  return {cases:Object.fromEntries(CASES.map(name=>[name,checks[name]])),
    browser:{binary:b.binary,dom:b.dom,sandbox:b.sandbox,
      domDetail:b.domDetail,sandboxDetail:b.sandboxDetail,namespaceProbe:b.namespaceProbe}};
}
export function runSandboxCompatibility({
  platform=process.platform,env=process.env,docker=shellDocker,
  name='ca-win-m47-'+randomBytes(6).toString('hex'),
}={}){
  const report={
    schemaVersion:1,stage:'M4.7-chromium-sandbox-on-compatibility',
    status:'BLOCKED',recordedAtUtc:new Date().toISOString(),
    imageTag:IMAGE_TAG,imageId:null,registryDigest:null,
    fixedGuestSha256:createHash('sha256').update(SOURCE).digest('hex'),
    seccompSha256:null,seccompUpstreamGitBlob:null,seccompUpstreamSha256:null,
    seccompVariant:null,
    checks:null,passedChecks:0,requiredChecks:CASES.length,
    browser:null,containerRemoved:false,
    modelInvoked:false,generatedSourceExecuted:false,sourceMounted:false,
    untrustedBrowserQualified:false,staticWebRuntimeQualified:false,
    generatedCodeExecutionEnabled:false,reason:null,
  };
  if(platform!=='win32'||!NAME.test(name)||env.DOCKER_HOST||env.DOCKER_CONTEXT){
    report.reason='Windows local Docker Desktop only; no context overrides';
    return report;
  }
  let attempted=false;
  try{
    const selection=docker(['context','show'],8000);
    if(selection.status!==0||!selection.stdout)
      fail('Docker Desktop context unavailable');
    const current=selection.stdout.trim();
    const context=json(docker,['context','inspect',current,
      '--format','{{json .}}'],'Docker context');
    const info=json(docker,['info','--format','{{json .}}'],'Docker info');
    validateDockerContext(current,context?.Endpoints?.docker?.Host,info);
    const meta=json(docker,['image','inspect',IMAGE_TAG,
      '--format','{{json .}}'],'Cached Playwright image');
    const pin=validatePinnedImage(meta);
    report.imageId=pin.imageId;
    report.registryDigest=pin.registryDigest;
    const checkedProfile=checkedChromiumSeccomp();
    report.seccompSha256=checkedProfile.sha256;
    report.seccompUpstreamGitBlob=checkedProfile.upstreamGitBlob;
    report.seccompUpstreamSha256=checkedProfile.upstreamSha256;
    report.seccompVariant=checkedProfile.variant;
    const collision=docker(['ps','-a','--filter','name=^/'+name+'$',
      '--format','{{.ID}}'],8000);
    if(collision.status!==0||collision.stdout.trim())
      fail('Disposable name collision or Docker preflight error');
    attempted=true;
    const guest=docker(sandboxDockerArgs(pin.imageId,name),110000,SOURCE);
    if(guest.timedOut)fail('Fixed sandbox compatibility probe timed out');
    if(!guest.stdout||!guest.stdout.trim())
      fail('No sandbox observations; docker exit='+
        (Number.isInteger(guest.status)?guest.status:'unavailable')+
        '; category='+classifyGuestFailure(guest));
    const findings=parseSandboxReport(guest.stdout);
    report.checks=findings.cases;
    report.browser=findings.browser;
    report.passedChecks=CASES.filter(k=>findings.cases[k]).length;
    if(report.passedChecks!==CASES.length)
      fail('Sandbox compatibility checks failed: '+
        CASES.filter(k=>!findings.cases[k]).join(', '));
    if(guest.status!==0)
      fail('Sandbox guest exited nonzero despite all observations; category='+
        classifyGuestFailure(guest));
    // "COMPATIBILITY_PASS" is not "untrusted sandbox QUALIFIED".
    report.status='SANDBOX_COMPATIBILITY_PASS';
  }catch(e){
    report.reason=String(e?.message??e).slice(0,260);
  }finally{
    if(attempted){
      try{
        const found=docker(['container','inspect',name,
          '--format','{{json .Config.Labels}}'],15000);
        if(found.status===0){
          const labels=JSON.parse(found.stdout.trim());
          if(labels?.['codingassistant.m47']!==name)
            fail('Container ownership label mismatch; refusing removal');
          docker(['rm','-f',name],15000);
        }
        const left=docker(['ps','-a','--filter','name=^/'+name+'$',
          '--format','{{.ID}}'],15000);
        report.containerRemoved=left.status===0 && left.stdout.trim()==='';
      }catch{report.containerRemoved=false;}
    }
    if(attempted&&!report.containerRemoved){
      report.status='BLOCKED';
      report.reason='Disposable container cleanup not confirmed';
    }
  }
  return report;
}
export function main(argv=process.argv.slice(2),options={}){
  if(argv.length===0){
    process.stdout.write('M4.7 WINDOWS_CHROMIUM_SANDBOX: NOT_RUN\n');
    process.stdout.write('Use --run for fixed browser compatibility only.\n');
    return 0;
  }
  if(argv.length!==1||argv[0]!=='--run'){
    process.stderr.write(
      'Usage: node tools/m4/windows_chromium_sandbox_qualification.mjs [--run]\n');
    return 2;
  }
  const report=runSandboxCompatibility(options);
  const evidence=writePrivateEvidence(report,options.evidenceFolder);
  process.stdout.write('M4.7 WINDOWS_'+report.status+'\n');
  process.stdout.write('  fixed isolation and browser checks: '+
    report.passedChecks+'/'+report.requiredChecks+'\n');
  process.stdout.write('  container removed: '+report.containerRemoved+'\n');
  if(report.seccompVariant)process.stdout.write('  seccomp fixture policy: '+report.seccompVariant+'\n');
  if(report.browser)
    process.stdout.write('  Chromium: '+report.browser.binary+
      '; fixed DOM: '+report.browser.dom+
      '; sandbox status: '+report.browser.sandbox+'\n');
  if(report.browser)process.stdout.write('  diagnostics: namespace='+report.browser.namespaceProbe+
    '; DOM='+report.browser.domDetail+'; sandbox='+report.browser.sandboxDetail+'\n');
  if(report.reason)process.stdout.write('  blocker: '+report.reason+'\n');
  process.stdout.write('  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED\n');
  process.stdout.write('Evidence: '+evidence+'\n');
  return report.status==='SANDBOX_COMPATIBILITY_PASS' && 
    report.containerRemoved ? 0:2;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
  process.exitCode=main();
