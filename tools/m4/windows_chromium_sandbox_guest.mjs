// M4.7 — fixed Chrome sandbox compatibility probe. NO generated source.
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

export const CASES=Object.freeze([
  'non_root_uid','linux_seccomp_active','host_credentials_absent',
  'engine_socket_absent','no_workspace_or_host_mount',
  'root_filesystem_write_denied','child_system_write_denied',
  'outbound_network_denied','child_outbound_network_denied',
  'sandboxed_chromium_fixed_dom','chromium_sandbox_status_adequate',
]);
export const BROWSER_KINDS=Object.freeze([
  'HEADLESS_SHELL','FULL_CHROMIUM','BINARY_NOT_FOUND','DISCOVERY_FAILED',
]);
export const BROWSER_CODES=Object.freeze([
  'PASS','BINARY_NOT_FOUND','DISCOVERY_FAILED',
  'CHROMIUM_TIMEOUT','CHROMIUM_SPAWN_ENOENT','CHROMIUM_SPAWN_EACCES',
  'CHROMIUM_SPAWN_ERROR','CHROMIUM_SHARED_LIBRARY_ERROR',
  'CHROMIUM_SANDBOX_REJECTED','CHROMIUM_RESOURCE_EXHAUSTED',
  'CHROMIUM_CRASH','CHROMIUM_CRASH_SIGSYS','CHROMIUM_CRASH_SIGTRAP',
  'CHROMIUM_CRASH_SIGSEGV','CHROMIUM_CRASH_SIGABRT','CHROMIUM_CRASH_SIGKILL',
  'CHROMIUM_CRASHPAD_ERROR','CHROMIUM_PROFILE_ERROR','CHROMIUM_EXIT_NONZERO',
  'CHROMIUM_FIXED_DOM_MISSING','CHROMIUM_SANDBOX_STATUS_UNPROVEN',
]);
// Deliberately absent: --no-sandbox, --disable-setuid-sandbox,
// --single-process, --no-zygote, --disable-seccomp-filter-sandbox.
export const CRASH_DETAILS=Object.freeze([
  'NONE','PID_LIMIT_HIT','MEMORY_LIMIT_HIT','NAMESPACE_CREDENTIALS_FATAL',
  'ZYGOTE_FATAL','SANDBOX_FATAL','THREAD_CREATION_FATAL','CRASHPAD_FATAL',
  'PROFILE_FATAL','OTHER_FATAL_PERMISSION','OTHER_FATAL','NO_FATAL_LOG',
]);
export const NAMESPACE_CODES=Object.freeze(['USER_NAMESPACE_OK','USER_NAMESPACE_DENIED',
  'USER_NAMESPACE_TOOL_UNAVAILABLE','USER_NAMESPACE_PROBE_FAILED']);
export const BROWSER_ARGS=Object.freeze([
  '--headless','--disable-gpu','--disable-dev-shm-usage',
  '--disable-background-networking','--disable-crash-reporter',
  '--no-first-run','--no-default-browser-check',
  '--enable-logging=stderr','--dump-dom',
]);
function denied(fn){try{fn();return false;}catch{return true;}}
function child(command){
  return spawnSync(process.execPath,['-e',command],{
    encoding:'utf8',timeout:4500,maxBuffer:1024,
    env:{HOME:'/tmp',PATH:process.env.PATH},
  });
}
function denyNetwork(){
  return new Promise(resolve=>{
    const s=net.createConnection({host:'203.0.113.1',port:443});
    let done=false;
    const end=x=>{if(done)return;done=true;s.destroy();resolve(x);};
    s.setTimeout(1300,()=>end(true));
    s.once('error',()=>end(true));
    s.once('connect',()=>end(false));
  });
}
function browserExecutable(){
  try{
    const names=fs.readdirSync('/ms-playwright')
      .filter(n=>/^chromium(?:_headless_shell)?-\d+$/.test(n)).sort();
    const candidates=[];
    // The full browser implements chrome://sandbox WebUI; old standalone
    // headless shell is not an equivalent status-page verifier.
    for(const name of names.filter(n=>n.startsWith('chromium-')))
      for(const rel of ['chrome-linux64/chrome','chrome-linux/chrome'])
        candidates.push({kind:'FULL_CHROMIUM',
          executable:path.posix.join('/ms-playwright',name,rel)});
    for(const name of names.filter(n=>n.startsWith('chromium_headless_shell-')))
      for(const rel of [
        'chrome-headless-shell-linux64/chrome-headless-shell',
        'chrome-linux/headless_shell',
      ])candidates.push({kind:'HEADLESS_SHELL',
        executable:path.posix.join('/ms-playwright',name,rel)});
    return candidates.find(item=>fs.existsSync(item.executable)) ??
      {kind:'BINARY_NOT_FOUND',executable:null};
  }catch{return {kind:'DISCOVERY_FAILED',executable:null};}
}
export function classifyBrowser(proc){
  if(proc.error?.code==='ETIMEDOUT')return 'CHROMIUM_TIMEOUT';
  if(proc.error?.code==='ENOENT')return 'CHROMIUM_SPAWN_ENOENT';
  if(proc.error?.code==='EACCES')return 'CHROMIUM_SPAWN_EACCES';
  if(proc.error)return 'CHROMIUM_SPAWN_ERROR';
  const stderr=String(proc.stderr??'').slice(0,8192);
  if(/crashpad.*(?:database|permission denied|read-only file system)|--database is required/i.test(stderr))
    return 'CHROMIUM_CRASHPAD_ERROR';
  if(/(?:profile|user data directory|SingletonLock).*(?:permission denied|read-only|failed|in use)|failed to create.*(?:profile|user data directory)/i.test(stderr))
    return 'CHROMIUM_PROFILE_ERROR';
  if(/error while loading shared libraries|cannot open shared object/i.test(stderr))
    return 'CHROMIUM_SHARED_LIBRARY_ERROR';
  if(/no usable sandbox|failed to move to new namespace|failed to create.*namespace|operation not permitted|sandbox.*(fail|denied)/i.test(stderr))
    return 'CHROMIUM_SANDBOX_REJECTED';
  if(/resource temporarily unavailable|no space left on device|out of memory/i.test(stderr))
    return 'CHROMIUM_RESOURCE_EXHAUSTED';
  if(['SIGSYS','SIGTRAP','SIGSEGV','SIGABRT','SIGKILL'].includes(proc.signal))
    return 'CHROMIUM_CRASH_'+proc.signal;
  if(proc.signal)return 'CHROMIUM_CRASH';
  return 'CHROMIUM_EXIT_NONZERO';
}
// Only observed rendered output counts, not a marker embedded in script source.
export function fixedDomPassed(html){
  return typeof html==='string' && /<output\s+id=["']result["']\s*>\s*CA_M47_SANDBOXED_DOM_OK\s*<\/output>/i
    .test(html.replace(/<script\b[^>]*>[\s\S]*?<\/script\s*>/gi,''));
}
export function sandboxStatusPassed(html){
  if(typeof html!=='string')return false;
  const body=html.replace(/<(script|style|template)\b[^>]*>[\s\S]*?<\/\1\s*>/gi,' ')
    .replace(/<!--[^]*?-->/g,' ').replace(/<[^>]+>/g,' ').replace(/\s+/g,' ');
  return /you are adequately sandboxed/i.test(body) &&
    !/you are not adequately sandboxed/i.test(body);
}
// Diagnostic-only read of this guest's kernel accounting; no policy change.
function eventCount(paths,key){
  for(const file of paths)try{
    const lines=fs.readFileSync(file,'utf8').split('\n');
    const line=lines.find(l=>l.startsWith(key+' '));
    const value=line?Number(line.slice(key.length+1)):NaN;
    if(Number.isSafeInteger(value)&&value>=0)return value;
  }catch{}
  return null;
}
function resourceEvents(){
  return {pids:eventCount(['/sys/fs/cgroup/pids.events','/sys/fs/cgroup/pids/pids.events'],'max'),
    oom:eventCount(['/sys/fs/cgroup/memory.events','/sys/fs/cgroup/memory/memory.events'],'oom'),
    oomKill:eventCount(['/sys/fs/cgroup/memory.events','/sys/fs/cgroup/memory/memory.events'],'oom_kill')};
}
export function crashDetail(proc,before={},after={}){
  if(proc.status===0)return 'NONE';
  const increased=key=>Number.isSafeInteger(before[key])&&Number.isSafeInteger(after[key])&&after[key]>before[key];
  if(increased('pids'))return 'PID_LIMIT_HIT';
  if(increased('oom')||increased('oomKill'))return 'MEMORY_LIMIT_HIT';
  const stderr=String(proc.stderr??'').slice(0,65536);
  if(/FATAL[^\n]*credentials\.cc/i.test(stderr))return 'NAMESPACE_CREDENTIALS_FATAL';
  if(/pthread_create|failed to create (?:a |new )?thread|thread creation failed/i.test(stderr))return 'THREAD_CREATION_FATAL';
  if(/FATAL[^\n]*zygote/i.test(stderr))return 'ZYGOTE_FATAL';
  if(/FATAL[^\n]*sandbox/i.test(stderr))return 'SANDBOX_FATAL';
  if(/crashpad.*(?:database|permission denied)|--database is required/i.test(stderr))return 'CRASHPAD_FATAL';
  if(/(?:SingletonLock|profile directory|user data directory).*(?:permission denied|failed|read-only)/i.test(stderr))return 'PROFILE_FATAL';
  if(/FATAL[^\n]*(?:permission denied|operation not permitted)/i.test(stderr))return 'OTHER_FATAL_PERMISSION';
  if(/FATAL|Check failed:/i.test(stderr))return 'OTHER_FATAL';
  return 'NO_FATAL_LOG';
}
function userNamespaceProbe(){
  // Fixed, harmless command in the already isolated guest. No UID mapping,
  // mount, capability addition or host namespace access.
  const result=spawnSync('/usr/bin/unshare',['--user','--','/usr/bin/true'],{
    encoding:'utf8',timeout:5000,maxBuffer:4096,env:{HOME:'/tmp',PATH:'/usr/bin:/bin'},
  });
  if(result.status===0)return 'USER_NAMESPACE_OK';
  if(result.error?.code==='ENOENT')return 'USER_NAMESPACE_TOOL_UNAVAILABLE';
  if(/operation not permitted|permission denied/i.test(String(result.stderr??'')))return 'USER_NAMESPACE_DENIED';
  return 'USER_NAMESPACE_PROBE_FAILED';
}
function runBrowser(browser,url,kind){
  if(!browser.executable)
    return {passed:false,code:browser.kind,detail:'NONE'};
  if(kind==='status' && browser.kind!=='FULL_CHROMIUM')
    return {passed:false,code:'CHROMIUM_SANDBOX_STATUS_UNPROVEN',detail:'NONE'};
  // Explicitly create private, writable directories in the existing bounded
  // guest tmpfs. Never mount the host or make the root filesystem writable.
  const home=fs.mkdtempSync('/tmp/ca-m47-'+kind+'-');
  const dirs=Object.fromEntries(['config','cache','runtime','profile'].map(name=>{
    const dir=path.join(home,name);fs.mkdirSync(dir,{mode:0o700});return [name,dir];
  }));
  const before=resourceEvents();
  const result=spawnSync(browser.executable,[...BROWSER_ARGS,'--user-data-dir='+dirs.profile,url],{
    encoding:'utf8',timeout:23000,maxBuffer:65536,
    env:{HOME:home,PATH:process.env.PATH,XDG_CONFIG_HOME:dirs.config,
      XDG_CACHE_HOME:dirs.cache,XDG_RUNTIME_DIR:dirs.runtime},
  });
  if(result.status!==0)
    return {passed:false,code:classifyBrowser(result),detail:crashDetail(result,before,resourceEvents())};
  if(typeof result.stdout!=='string'||result.stdout.length>32768)
    return {passed:false,code:'CHROMIUM_FIXED_DOM_MISSING',detail:'NONE'};
  if(kind==='dom'){
    const passed=fixedDomPassed(result.stdout);
    return {passed,code:passed?'PASS':'CHROMIUM_FIXED_DOM_MISSING',detail:'NONE'};
  }
  // Ambiguous status-page responses are BLOCKED, not accepted as sandboxed.
  const passed=sandboxStatusPassed(result.stdout);
  return {passed,code:passed?'PASS':'CHROMIUM_SANDBOX_STATUS_UNPROVEN',detail:'NONE'};
}
export async function runFixedCompatibility(){
  const cases={};
  cases.non_root_uid=typeof process.getuid==='function' && process.getuid()===1000;
  cases.linux_seccomp_active=/^Seccomp:\s*2\s*$/m.test(
    fs.readFileSync('/proc/self/status','utf8'));
  cases.host_credentials_absent=[
    'OPENAI_API_KEY','ANTHROPIC_API_KEY','GITHUB_TOKEN','GH_TOKEN',
    'CODINGASSISTANT_OWNER_TOKEN','OLLAMA_HOST','AWS_SECRET_ACCESS_KEY',
  ].every(key=>!Object.hasOwn(process.env,key));
  cases.engine_socket_absent=!fs.existsSync('/var/run/docker.sock') &&
    !fs.existsSync('/run/podman/podman.sock');
  cases.no_workspace_or_host_mount=!fs.existsSync('/workspace') &&
    !fs.existsSync('/host') && !fs.existsSync('/run/desktop/mnt/host/c');
  cases.root_filesystem_write_denied=denied(()=>
    fs.writeFileSync('/etc/ca-m47-forbidden','BAD'));
  const sys=child("try{require('node:fs').writeFileSync('/etc/ca-m47-child','BAD');"+
    "process.stdout.write('BAD')}catch{process.stdout.write('DENIED')}");
  cases.child_system_write_denied=sys.status===0&&sys.stdout==='DENIED';
  cases.outbound_network_denied=await denyNetwork();
  const outbound=child(
    "const s=require('node:net').connect({host:'203.0.113.1',port:443});"+
    "let done=false;function finish(v){if(done)return;done=true;"+
    "process.stdout.write(v);s.destroy()}"+
    "s.setTimeout(800,()=>finish('DENIED'));"+
    "s.on('error',()=>finish('DENIED'));s.on('connect',()=>finish('BAD'))"
  );
  cases.child_outbound_network_denied=
    outbound.status===0&&outbound.stdout==='DENIED';
  const sample='/tmp/ca-m47-sandbox.html';
  fs.writeFileSync(sample,
    '<!doctype html><html><body><output id="result">PENDING</output>'+
    '<script>document.querySelector("#result").textContent='+
    '"CA_M47_SANDBOXED_DOM_OK"</script></body></html>',
    {flag:'wx',mode:0o600});
  const namespaceProbe=userNamespaceProbe();
  const browser=browserExecutable();
  const dom=runBrowser(browser,'file://'+sample,'dom');
  const sandbox=runBrowser(browser,'chrome://sandbox','status');
  cases.sandboxed_chromium_fixed_dom=dom.passed;
  cases.chromium_sandbox_status_adequate=sandbox.passed;
  return {cases,browser:{
    binary:browser.kind,dom:dom.code,sandbox:sandbox.code,
    domDetail:dom.detail,sandboxDetail:sandbox.detail,namespaceProbe,
  }};
}
if(process.argv.includes('--run-fixed-sandbox-probe')){
  try{
    const report=await runFixedCompatibility();
    if(Object.keys(report.cases).length!==CASES.length ||
       !CASES.every(k=>typeof report.cases[k]==='boolean') ||
       !BROWSER_KINDS.includes(report.browser.binary) ||
       !BROWSER_CODES.includes(report.browser.dom) ||
       !BROWSER_CODES.includes(report.browser.sandbox) ||
       !CRASH_DETAILS.includes(report.browser.domDetail) ||
       !CRASH_DETAILS.includes(report.browser.sandboxDetail) ||
       !NAMESPACE_CODES.includes(report.browser.namespaceProbe))
      throw Error('Invalid fixed guest schema');
    process.stdout.write(JSON.stringify({
      schemaVersion:1,cases:report.cases,browser:report.browser,
    })+'\n');
    if(!CASES.every(k=>report.cases[k]))process.exitCode=2;
  }catch{
    process.stdout.write(JSON.stringify({blocked:true})+'\n');
    process.exitCode=2;
  }
}
