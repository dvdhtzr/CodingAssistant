import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {spawnSync} from 'node:child_process';
import {
  sandboxDockerArgs,parseSandboxReport,runSandboxCompatibility,main,
  OFFICIAL_SECCOMP_GIT_BLOB,checkedChromiumSeccomp,validateChromiumSeccompBytes,
  validateFixtureSeccompBytes,FIXTURE_SECCOMP_VARIANT,FIXTURE_PID_LIMIT,
} from '../tools/m4/windows_chromium_sandbox_qualification.mjs';
import {
  CASES,BROWSER_ARGS,BROWSER_CODES,BROWSER_KINDS,classifyBrowser,fixedDomPassed,sandboxStatusPassed,crashDetail,outputDetail,boundedBrowserOutput,DOM_OUTPUT_LIMIT,SANDBOX_OUTPUT_LIMIT,fixedBrowserArgs,
} from '../tools/m4/windows_chromium_sandbox_guest.mjs';

const NAME='ca-win-m47-012345abcdef';
const ID='sha256:'+'a'.repeat(64);
const DIGEST='mcr.microsoft.com/playwright@sha256:'+'b'.repeat(64);
const META={Id:ID,RepoDigests:[DIGEST],Os:'linux',Architecture:'amd64'};
const INFO={OSType:'linux',OperatingSystem:'Docker Desktop',
  PidsLimit:true,MemoryLimit:true};
const GOOD=Object.fromEntries(CASES.map(key=>[key,true]));
const BROWSER={binary:'FULL_CHROMIUM',dom:'PASS',sandbox:'PASS',
  domDetail:'NONE',sandboxDetail:'NONE',namespaceProbe:'USER_NAMESPACE_OK'};
function envelope(cases=GOOD,browser=BROWSER){
  return {schemaVersion:1,cases,browser};
}
function fakeDocker(overrides={}){
  const calls=[];let ps=0;
  function docker(args,timeout,input){
    calls.push({args,timeout,input});
    if(args[0]==='context'&&args[1]==='show')
      return {status:0,stdout:overrides.context??'desktop-linux'};
    if(args[0]==='context'&&args[1]==='inspect')
      return {status:0,stdout:JSON.stringify({
        Endpoints:{docker:{Host:overrides.remote?
          'tcp://other-host:2375':'npipe:////./pipe/dockerDesktopLinuxEngine'}},
      })};
    if(args[0]==='info')return {status:0,stdout:JSON.stringify(INFO)};
    if(args[0]==='image')return {
      status:overrides.missingImage?1:0,stdout:JSON.stringify(META),
    };
    if(args[0]==='ps'){
      ps++;
      return {status:overrides.psError?1:0,
        stdout:ps===1?(overrides.collision?'existing':''):
          (overrides.leftover?'leftover':'')};
    }
    if(args[0]==='run')return {
      status:overrides.exit??0,
      stdout:overrides.stdout??JSON.stringify(
        envelope(overrides.cases??GOOD,overrides.browser??BROWSER)),
      stderr:overrides.stderr??'',
      timedOut:overrides.timedOut??false,
    };
    if(args[0]==='container'&&args[1]==='inspect')
      return {status:overrides.absent?1:0,stdout:JSON.stringify({
        'codingassistant.m47':overrides.badLabel?'somebody-else':NAME,
      })};
    if(args[0]==='rm')return {status:0,stdout:''};
    throw Error('Unexpected Docker command '+args.join(' '));
  }
  return {docker,calls};
}
test('sandbox probe fixed guest has 11 distinct documented checks',()=>{
  assert.equal(CASES.length,11);
  assert.equal(new Set(CASES).size,CASES.length);
  const guest=fs.readFileSync(new URL(
    '../tools/m4/windows_chromium_sandbox_guest.mjs',import.meta.url),'utf8');
  assert.ok(guest.includes('chrome://sandbox'));
  assert.ok(guest.includes('CA_M47_SANDBOXED_DOM_OK'));
  for(const k of CASES)assert.ok(guest.includes(k),k);
  for(const p of [
    'tools/m4/windows_chromium_sandbox_guest.mjs',
    'tools/m4/windows_chromium_sandbox_qualification.mjs',
  ]){
    const r=spawnSync(process.execPath,['--check',p],{
      encoding:'utf8',timeout:5000,
    });
    assert.equal(r.status,0,p+': '+r.stderr);
  }
});

test('browser runs without dangerous sandbox-disabling switches',()=>{
  for(const bypass of ['no-sandbox','disable-setuid-sandbox','no-zygote',
    'single-process','disable-seccomp-filter-sandbox'])
    assert.ok(!BROWSER_ARGS.some(a=>a.includes(bypass)),bypass);
  assert.ok(BROWSER_ARGS.includes('--dump-dom'));
  assert.ok(!BROWSER_ARGS.some(a=>a.startsWith('http:')));
  assert.ok(BROWSER_CODES.includes('CHROMIUM_SANDBOX_REJECTED'));
  assert.ok(BROWSER_KINDS.includes('HEADLESS_SHELL'));
});
test('Docker contract has immutable offline image and bounded no-mount isolation',()=>{
  const argv=sandboxDockerArgs(ID,NAME);
  for(const required of ['--rm','-i','--pull=never',
    '--network=none','--read-only','--cap-drop=all',
    '--security-opt=no-new-privileges','--user=1000:1000',
    '--pids-limit=256','--memory=2g','--cpus=2',
    '--ipc=private','--shm-size=256m',
    '--input-type=module','-','--run-fixed-sandbox-probe'])
    assert.ok(argv.includes(required),required);
  assert.ok(argv.includes(ID));
  assert.equal(FIXTURE_PID_LIMIT,256);
  assert.deepEqual(argv.filter(a=>a.startsWith('--pids-limit=')),['--pids-limit=256']);
  const seccomp=checkedChromiumSeccomp();
  const opts=argv.filter(a=>a.startsWith('--security-opt=seccomp='));
  assert.deepEqual(opts,['--security-opt=seccomp='+seccomp.path]);
  assert.ok(!opts.some(x=>x.includes('unconfined')));
  assert.equal(argv.filter(a=>a==='--mount').length,0);
  for(const bad of ['--privileged','--pid=host','--ipc=host',
    '--network=host','--cap-add','--env-file','--mount','-v',
    '--volume','--device','--publish','--security-opt=seccomp=unconfined',
    '--security-opt=apparmor=unconfined'])
    assert.ok(!argv.includes(bad),bad);
  assert.ok(!argv.some(a=>a.startsWith('--pid=')));
  assert.throws(()=>sandboxDockerArgs('mcr:latest',NAME));
  assert.throws(()=>sandboxDockerArgs(ID,'unapproved-name'));
});
test('exact upstream Playwright seccomp profile is pinned and fails on tampering',()=>{
  const attested=checkedChromiumSeccomp();
  assert.equal(attested.upstreamGitBlob,
    'fddc05fb520affb145404e6f6f647ca96af8087d');
  assert.equal(attested.upstreamGitBlob,OFFICIAL_SECCOMP_GIT_BLOB);
  assert.match(attested.sha256,/^[0-9a-f]{64}$/);
  assert.match(attested.path,/m47-fixed-chromium-chroot-seccomp\.json$/);
  assert.equal(attested.variant,FIXTURE_SECCOMP_VARIANT);
  const original=fs.readFileSync(new URL('../tools/m4/profiles/playwright-chromium-seccomp.json',import.meta.url));
  const normalized=Buffer.from(original.toString('utf8').replace(/\r\n/g,'\n'));
  assert.deepEqual(validateChromiumSeccompBytes(original),
    validateChromiumSeccompBytes(normalized));
  assert.deepEqual(validateChromiumSeccompBytes(
    Buffer.from(normalized.toString('utf8').replace(/\n/g,'\r\n'))),
    validateChromiumSeccompBytes(normalized));
  const p=JSON.parse(normalized.toString('utf8'));
  assert.equal(p.defaultAction,'SCMP_ACT_ERRNO');
  assert.deepEqual(p.syscalls[0].names,['clone','setns','unshare']);
  assert.equal(p.syscalls[0].action,'SCMP_ACT_ALLOW');
  assert.ok(p.syscalls.every(rule=>rule.action==='SCMP_ACT_ALLOW'));
  for(const bad of [
    Buffer.alloc(0),
    Buffer.from('{"defaultAction":"SCMP_ACT_ALLOW"}'),
    Buffer.from(normalized.toString('utf8').replace(
      '"defaultAction": "SCMP_ACT_ERRNO"',
      '"defaultAction": "SCMP_ACT_ALLOW"')),
    Buffer.from(normalized.toString('utf8').replace(
      '"setns",','"mount",')),
    Buffer.from(normalized.toString('utf8')+' '),
  ])assert.throws(()=>validateChromiumSeccompBytes(bad));
});
test('strict report rejects malformed, oversized, unknown, and forged categories',()=>{
  assert.deepEqual(parseSandboxReport(JSON.stringify(envelope())),
    {cases:GOOD,browser:BROWSER});
  const negative={...GOOD,chromium_sandbox_status_adequate:false};
  assert.equal(parseSandboxReport(JSON.stringify(
    envelope(negative,{...BROWSER,sandbox:'CHROMIUM_SANDBOX_STATUS_UNPROVEN'})))
    .cases.chromium_sandbox_status_adequate,false);
  for(const raw of [
    '', '{}','[]','false','{"blocked":true}',
    JSON.stringify({...envelope(),extra:'injected'}),
    JSON.stringify({...envelope(),schemaVersion:2}),
    JSON.stringify({...envelope(),cases:{...GOOD,extra:true}}),
    JSON.stringify({...envelope(),cases:{...GOOD,non_root_uid:'true'}}),
    JSON.stringify({...envelope(),browser:{...BROWSER,dom:'raw log'}}),
    JSON.stringify(envelope(GOOD,{...BROWSER,binary:'HEADLESS_SHELL'})),
    JSON.stringify(envelope(GOOD,{...BROWSER,domDetail:'raw secret'})),
    JSON.stringify(envelope(GOOD,{...BROWSER,namespaceProbe:'raw secret'})),
    JSON.stringify(envelope(GOOD,{...BROWSER,dom:'CHROMIUM_CRASH_SIGTRAP'})),
    JSON.stringify({...envelope(),browser:{...BROWSER,path:'/home/user'}}),
    JSON.stringify({...envelope(),browser:{...BROWSER,sandbox:0}}),
    '{unclosed',
    'x'.repeat(8000),
  ])assert.throws(()=>parseSandboxReport(raw),raw.slice(0,60));
});
test('Chrome error categorizer emits only allowlisted codes and no stderr',()=>{
  const cases=[
    [{error:{code:'ETIMEDOUT'}},'CHROMIUM_TIMEOUT'],
    [{error:{code:'ENOENT'}},'CHROMIUM_SPAWN_ENOENT'],
    [{error:{code:'EACCES'}},'CHROMIUM_SPAWN_EACCES'],
    [{error:{code:'EIO'}},'CHROMIUM_SPAWN_ERROR'],
    [{stderr:'error while loading shared libraries SECRET'},'CHROMIUM_SHARED_LIBRARY_ERROR'],
    [{stderr:'Failed to move to new namespace: Operation not permitted SECRET'},
      'CHROMIUM_SANDBOX_REJECTED'],
    [{stderr:'no usable sandbox!'},'CHROMIUM_SANDBOX_REJECTED'],
    [{stderr:'Resource temporarily unavailable'},'CHROMIUM_RESOURCE_EXHAUSTED'],
    [{signal:'SIGSYS',stderr:'SECRET'},'CHROMIUM_CRASH_SIGSYS'],
    [{signal:'SIGTRAP',stderr:'SECRET'},'CHROMIUM_CRASH_SIGTRAP'],
    [{signal:'SIGABRT',stderr:'SECRET'},'CHROMIUM_CRASH_SIGABRT'],
    [{signal:'SIGTRAP',stderr:'chrome_crashpad_handler: --database is required SECRET'},'CHROMIUM_CRASHPAD_ERROR'],
    [{status:1,stderr:'SECRET'},'CHROMIUM_EXIT_NONZERO'],
  ];
  for(const [input,expected] of cases){
    const code=classifyBrowser(input);
    assert.equal(code,expected);
    assert.ok(BROWSER_CODES.includes(code));
    assert.ok(!code.includes('SECRET'));
  }
});
test('simulated positive Windows run never sets untrusted execution permission',()=>{
  const mock=fakeDocker();
  const result=runSandboxCompatibility({platform:'win32',
    env:{},name:NAME,docker:mock.docker});
  assert.equal(result.status,'SANDBOX_COMPATIBILITY_PASS');
  assert.equal(result.passedChecks,11);
  assert.equal(result.containerRemoved,true);
  assert.deepEqual(result.browser,BROWSER);
  assert.equal(result.seccompVariant,FIXTURE_SECCOMP_VARIANT);
  assert.equal(result.fixturePidLimit,256);
  assert.equal(result.seccompSha256,checkedChromiumSeccomp().sha256);
  assert.equal(result.seccompUpstreamSha256,checkedChromiumSeccomp().upstreamSha256);
  for(const prop of [
    'modelInvoked','generatedSourceExecuted','sourceMounted',
    'untrustedBrowserQualified','staticWebRuntimeQualified',
    'generatedCodeExecutionEnabled'])
    assert.equal(result[prop],false,prop);
  const run=mock.calls.filter(x=>x.args[0]==='run');
  assert.equal(run.length,1);
  assert.equal(run[0].input,fs.readFileSync(new URL(
    '../tools/m4/windows_chromium_sandbox_guest.mjs',import.meta.url),'utf8'));
  assert.ok(!run[0].args.includes(run[0].input));
  assert.equal(mock.calls.filter(x=>x.args[0]==='rm').length,1);
  assert.ok(!mock.calls.some(x=>x.args[0]==='pull'||x.args[0]==='build'));
});
test('each failed sandbox/egress/secret/child/browser check blocks and cleans up',()=>{
  for(const key of CASES){
    const bad={...GOOD,[key]:false};
    const mock=fakeDocker({cases:bad,exit:2,
      browser:key==='chromium_sandbox_status_adequate'?
        {...BROWSER,sandbox:'CHROMIUM_SANDBOX_STATUS_UNPROVEN'}:BROWSER});
    const result=runSandboxCompatibility({platform:'win32',
      env:{},name:NAME,docker:mock.docker});
    assert.equal(result.status,'BLOCKED',key);
    assert.equal(result.passedChecks,10,key);
    assert.equal(result.containerRemoved,true,key);
    assert.match(result.reason,new RegExp(key));
    assert.equal(result.generatedCodeExecutionEnabled,false);
  }
});
test('missing image, remote Docker, collision and host overrides never run',()=>{
  for(const change of [{remote:true},{missingImage:true},{collision:true}]){
    const mock=fakeDocker(change);
    const r=runSandboxCompatibility({platform:'win32',env:{},
      name:NAME,docker:mock.docker});
    assert.equal(r.status,'BLOCKED');
    assert.equal(mock.calls.filter(x=>x.args[0]==='run').length,0);
  }
  const mock=fakeDocker();
  for(const options of [
    {platform:'linux',env:{}},{platform:'darwin',env:{}},
    {platform:'win32',env:{DOCKER_HOST:'tcp://bad'}},
    {platform:'win32',env:{DOCKER_CONTEXT:'remote'}},
  ])assert.equal(runSandboxCompatibility({
    ...options,name:NAME,docker:mock.docker}).status,'BLOCKED');
  assert.equal(mock.calls.length,0);
});
test('malformed guest, nonzero status, timeout, and ownership cleanup fail closed',()=>{
  for(const change of [
    {stdout:'{"blocked":true}',exit:2},
    {stdout:'',exit:125,stderr:'SECRET_PATH'},
    {exit:2},{timedOut:true},
    {badLabel:true},{leftover:true},{psError:true},
  ]){
    const mock=fakeDocker(change);
    const result=runSandboxCompatibility({
      platform:'win32',env:{},name:NAME,docker:mock.docker,
    });
    assert.equal(result.status,'BLOCKED',JSON.stringify(change));
    assert.equal(result.generatedCodeExecutionEnabled,false);
    assert.ok(!String(result.reason).includes('SECRET_PATH'));
  }
});
test('no arguments is NOT_RUN and never contacts Docker',()=>{
  const processResult=spawnSync(process.execPath,
    ['tools/m4/windows_chromium_sandbox_qualification.mjs'],{
      encoding:'utf8',timeout:5000,
    });
  assert.equal(processResult.status,0,processResult.stderr);
  assert.match(processResult.stdout,/NOT_RUN/);
});

test('rendered fixture must change actual output; script source is not a pass',()=>{
  assert.equal(fixedDomPassed('<output id="result">PENDING</output><script>document.querySelector("#result").textContent="CA_M47_SANDBOXED_DOM_OK"</script>'),false);
  assert.equal(fixedDomPassed('<script>const x=\'<output id="result">CA_M47_SANDBOXED_DOM_OK</output>\'</script>'),false);
  assert.equal(fixedDomPassed('<output id="result">CA_M47_SANDBOXED_DOM_OK</output>'),true);
  assert.equal(sandboxStatusPassed('<script>const status="You are adequately sandboxed."</script>'),false);
  assert.equal(sandboxStatusPassed('<p>You are not adequately sandboxed!</p>'),false);
  assert.equal(sandboxStatusPassed('<p>You are adequately sandboxed.</p>'),true);
});

test('crash details distinguish kernel resource limits from sandbox startup without returning raw logs',()=>{
  assert.equal(crashDetail({status:null,stderr:'SECRET'},{pids:1},{pids:2}),'PID_LIMIT_HIT');
  assert.equal(crashDetail({status:null},{oom:0},{oom:1}),'MEMORY_LIMIT_HIT');
  assert.equal(crashDetail({status:null,stderr:'FATAL:sandbox/linux/services/credentials.cc:137] Check failed: Permission denied (13) SECRET'}),'NAMESPACE_CREDENTIALS_FATAL');
  assert.equal(crashDetail({status:null,stderr:'pthread_create: Resource temporarily unavailable SECRET'}),'THREAD_CREATION_FATAL');
  assert.equal(crashDetail({status:null,stderr:'FATAL:zygote_host_impl_linux.cc:126 SECRET'}),'ZYGOTE_FATAL');
  assert.equal(crashDetail({status:null,stderr:'SECRET'}),'NO_FATAL_LOG');
  assert.equal(crashDetail({status:0},{pids:0},{pids:1}),'PID_LIMIT_HIT');
  assert.equal(crashDetail({status:0},{pids:0},{pids:0}),'NONE');
});

test('owner-approved fixture policy differs by exactly one unconditional chroot syscall rule',()=>{
  const original=fs.readFileSync(new URL('../tools/m4/profiles/playwright-chromium-seccomp.json',import.meta.url));
  const checked=checkedChromiumSeccomp();
  const derived=fs.readFileSync(checked.path);
  const p=JSON.parse(original.toString('utf8')), q=JSON.parse(derived.toString('utf8'));
  const extra=q.syscalls.pop();
  assert.deepEqual(q,p);
  assert.deepEqual(extra.names,['chroot']);
  assert.equal(extra.action,'SCMP_ACT_ALLOW');
  assert.deepEqual(extra.args,[]);
  assert.deepEqual(extra.includes,{});
  assert.deepEqual(extra.excludes,{});
  assert.equal(p.defaultAction,'SCMP_ACT_ERRNO');
  assert.equal(validateFixtureSeccompBytes(original,derived).sha256,checked.sha256);
  assert.deepEqual(validateFixtureSeccompBytes(original,Buffer.from(derived.toString('utf8').replace(/\r?\n/g,'\r\n'))),validateFixtureSeccompBytes(original,derived));
  for(const mutate of [
    x=>x.defaultAction='SCMP_ACT_ALLOW',
    x=>x.syscalls.at(-1).names.push('mount'),
    x=>x.syscalls.at(-1).names=['pivot_root'],
    x=>x.syscalls.splice(0,1),
    x=>x.syscalls.pop(),
  ]){
    const changed=JSON.parse(derived.toString('utf8'));mutate(changed);
    assert.throws(()=>validateFixtureSeccompBytes(original,Buffer.from(JSON.stringify(changed,null,2)+'\n')));
  }
  assert.throws(()=>validateFixtureSeccompBytes(Buffer.from(original.toString('utf8')+' '),derived));
});

test('normal exit without rendered evidence yields actionable bounded diagnostics',()=>{
  const result=(stdout,stderr='')=>({status:0,stdout,stderr});
  assert.equal(outputDetail(result(''),'dom'),'EMPTY_DOM_OUTPUT');
  assert.equal(outputDetail(result('<html><body></body></html>'),'dom'),'BLANK_DOM_OUTPUT');
  assert.equal(outputDetail(result('<output id="result">PENDING</output>'),'dom'),'FIXTURE_PENDING');
  assert.equal(outputDetail(result('<div id="main-frame-error">SECRET</div>'),'dom'),'BROWSER_ERROR_PAGE');
  assert.equal(outputDetail(result('<h1>Sandbox Status</h1>'),'status'),'SANDBOX_STATUS_PENDING');
  assert.equal(outputDetail(result('<p>You are not adequately sandboxed.</p>'),'status'),'SANDBOX_NEGATIVE');
  assert.equal(outputDetail(result('','pthread_create: Resource temporarily unavailable SECRET'),'dom'),'RENDERER_THREAD_FAILED');
  assert.equal(outputDetail(result('','zygote failed SECRET'),'dom'),'NAMESPACE_CHILD_FAILED');
  assert.equal(outputDetail(result('','renderer process crashed SECRET'),'dom'),'RENDERER_PROCESS_FAILED');
  assert.equal(outputDetail(result(''),'status',{pids:0},{pids:1}),'PID_LIMIT_HIT');
  assert.equal(outputDetail(result(''),'status',{oom:0},{oom:1}),'MEMORY_LIMIT_HIT');
  assert.equal(outputDetail(result('x'.repeat(40000)),'dom'),'DOM_OUTPUT_OVERSIZED');
  assert.equal(outputDetail(result('<p>SECRET</p>'),'dom'),'UNEXPECTED_DOM_OUTPUT');
  assert.ok(BROWSER_ARGS.includes('--virtual-time-budget=5000'));
  assert.ok(BROWSER_ARGS.includes('--timeout=12000'));
});

test('sandbox status has its own bounded capture ceiling without weakening evidence assertions',()=>{
  assert.equal(DOM_OUTPUT_LIMIT,32768);
  assert.equal(SANDBOX_OUTPUT_LIMIT,262144);
  const padding='<style>/*'+'x'.repeat(40000)+'*/</style>';
  const yes=padding+'<p>You are adequately sandboxed.</p>';
  const no=padding+'<p>You are not adequately sandboxed.</p>';
  const scriptOnly=padding+'<script>const msg="You are adequately sandboxed."</script>';
  assert.equal(boundedBrowserOutput(yes,'dom'),false);
  assert.equal(boundedBrowserOutput(yes,'status'),true);
  assert.equal(sandboxStatusPassed(yes),true);
  assert.equal(sandboxStatusPassed(no),false);
  assert.equal(sandboxStatusPassed(scriptOnly),false);
  assert.equal(outputDetail({status:0,stdout:no},'status'),'SANDBOX_NEGATIVE');
  assert.equal(outputDetail({status:0,stdout:'x'.repeat(SANDBOX_OUTPUT_LIMIT+1)},'status'),'DOM_OUTPUT_OVERSIZED');
  assert.equal(boundedBrowserOutput('é'.repeat(16385),'dom'),false);
});

test('headless internal-scheme permission is restricted to the fixed sandbox-status invocation',()=>{
  const dom=fixedBrowserArgs('dom','/tmp/fixture-profile');
  const status=fixedBrowserArgs('status','/tmp/status-profile');
  assert.deepEqual(dom,[...BROWSER_ARGS,'--user-data-dir=/tmp/fixture-profile','file:///tmp/ca-m47-sandbox.html']);
  assert.deepEqual(status,[...BROWSER_ARGS,'--allow-chrome-scheme-url','--user-data-dir=/tmp/status-profile','chrome://sandbox']);
  assert.ok(!BROWSER_ARGS.includes('--allow-chrome-scheme-url'));
  assert.ok(!dom.includes('--allow-chrome-scheme-url'));
  for(const args of [dom,status]){
    for(const bypass of ['--no-sandbox','--disable-setuid-sandbox','--single-process','--no-zygote','--disable-seccomp-filter-sandbox','--disable-web-security'])
      assert.ok(!args.includes(bypass));
    assert.ok(!args.some(a=>a.startsWith('--remote-debugging')));
  }
  assert.throws(()=>fixedBrowserArgs('https://example.com','/tmp/status-profile'));
});
