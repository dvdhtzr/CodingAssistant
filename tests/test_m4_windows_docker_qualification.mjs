import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  IMAGE_TAG, dockerArgs, parseObservations, parseGuestReport,
  classifyGuestFailure, validateDockerContext,
  validatePinnedImage, runFixture, writePrivateEvidence, main,
} from '../tools/m4/windows_docker_qualification.mjs';
import { CASES, BROWSER_CODES, BROWSER_KINDS, browserCandidatePaths,
  classifyBrowserFailure } from '../tools/m4/windows_docker_guest.mjs';

const IMAGE_ID = 'sha256:' + 'a'.repeat(64);
const DIGEST = 'mcr.microsoft.com/playwright@sha256:' + 'b'.repeat(64);
const NAME = 'ca-win-m4-012345abcdef';
const IMAGE = {
  Id: IMAGE_ID, RepoDigests: [DIGEST], Os: 'linux', Architecture: 'amd64',
};
const INFO = {
  OSType: 'linux', OperatingSystem: 'Docker Desktop 5.0',
  PidsLimit: true, MemoryLimit: true,
};
const goodCases = Object.fromEntries(CASES.map(name => [name, true]));
const goodBrowser = {binary:'HEADLESS_SHELL', working:'PASS', broken:'PASS'};
function guestEnvelope(cases = goodCases, browser = goodBrowser) {
  return {schemaVersion:2,cases,browser};
}
function result(status, stdout = '') { return { status, stdout }; }
function fakeDocker(overrides = {}) {
  const calls = [];
  const streamedInputs = [];
  let psCalls = 0;
  function docker(args, _timeout, input) {
    calls.push(args);
    if (args[0] === 'run') streamedInputs.push(input);
    if (args[0] === 'context' && args[1] === 'show')
      return result(0, overrides.contextName ?? 'desktop-linux');
    if (args[0] === 'context' && args[1] === 'inspect')
      return result(0, JSON.stringify({ Endpoints: {
        docker: { Host: overrides.endpoint ?? 'npipe:////./pipe/dockerDesktopLinuxEngine' },
      } }));
    if (args[0] === 'info')
      return result(0, JSON.stringify(overrides.info ?? INFO));
    if (args[0] === 'image' && args[1] === 'inspect')
      return result(overrides.imageStatus ?? 0,
        JSON.stringify(overrides.image ?? IMAGE));
    if (args[0] === 'run') return {
      ...result(overrides.guestStatus ?? 0,
        overrides.guestOutput ??
        JSON.stringify(overrides.report ?? guestEnvelope(
          overrides.cases ?? goodCases, overrides.browser ?? goodBrowser))),
      timedOut: overrides.timeout ?? false,
      stderr: overrides.guestStderr ?? '',
      errorCode: overrides.guestErrorCode ?? null,
    };
    if (args[0] === 'container' && args[1] === 'inspect')
      return result(overrides.ownerMissing ? 1 : 0,
        JSON.stringify({ 'codingassistant.m4': overrides.ownerLabel ?? NAME }));
    if (args[0] === 'rm') return result(0);
    if (args[0] === 'ps') {
      psCalls += 1;
      return psCalls === 1
        ? result(overrides.collisionStatus ?? 0, overrides.collisionOutput ?? '')
        : result(overrides.cleanupStatus ?? 0, overrides.cleanupOutput ?? '');
    }
    throw new Error('Unexpected fake Docker operation: ' + args.join(' '));
  }
  return { docker, calls, streamedInputs };
}

test('fixed guest is pure Node syntax and never runs on import', () => {
  assert.equal(CASES.length, 18);
  assert.equal(new Set(CASES).size, CASES.length);
  const guest = readFileSync(new URL('../tools/m4/windows_docker_guest.mjs',
    import.meta.url), 'utf8');
  for (const required of [
    '--check', 'chromium_working_fixture_executed',
    'chromium_broken_fixture_stayed_inert', 'data-acceptance',
    "fs.symlinkSync('/etc/passwd'", '/tmp/../etc/ca-m4-traversal',
    'readFileSync', '203.0.113.1', 'child_outbound_tcp_denied',
    'child_system_write_denied', 'ls-remote', 'docker.sock',
  ]) assert.ok(guest.includes(required), 'Missing guest condition ' + required);
  for (const forbidden of [
    'docker run', 'git push', 'OpenCode', 'session/prompt',
    'http://host.docker.internal', 'localhost:11434',
    'npm install', 'npx ',
  ]) assert.ok(!guest.includes(forbidden), 'Unsafe guest path ' + forbidden);
  const checked = spawnSync(process.execPath, ['--check',
    'tools/m4/windows_docker_guest.mjs'], {
    encoding: 'utf8', timeout: 5000,
  });
  assert.equal(checked.status, 0, checked.stderr);
});

test('Docker argv permits exactly one pinned no-network no-mount guest', () => {
  const args = dockerArgs(IMAGE_ID, NAME);
  for (const must of [
    '--pull=never', '--network=none', '--read-only', '--cap-drop=all',
    '--security-opt=no-new-privileges', '--user=1000:1000',
    '--pids-limit=96', '--memory=2g', '--cpus=2', '--ipc=private',
    '--init', '-i', '--entrypoint=node',
    '--input-type=module', '--run-fixed-guest',
  ]) assert.ok(args.includes(must), 'Missing Docker guard ' + must);
  for (const mustNot of [
    '--volume', '-v', '--mount', '--privileged', '--network=host',
    '--publish', '-p', '--device', '--env-file', '--cap-add',
    '--security-opt=seccomp=unconfined', '--user=0',
  ]) assert.ok(!args.includes(mustNot), 'Unauthorized Docker flag ' + mustNot);
  assert.equal(args.filter(x => x === '--run-fixed-guest').length, 1);
  assert.equal(args[args.indexOf('--entrypoint=node') + 1], IMAGE_ID);
  assert.ok(args.includes('--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m'));
  assert.ok(!args.some(arg => arg.startsWith('--pid=')),
    'Docker default is a private PID namespace; explicit --pid=private is invalid');
  assert.ok(!args.includes('--pid=host'), 'Never share host PID namespace');
  assert.ok(!args.includes('-e'), 'Source must not be passed as command argument');
  assert.ok(!args.includes('--'), 'Extra Node CLI terminator is unnecessary');
  assert.equal(args.at(-2), '-', 'Node must evaluate trusted stdin script');
  for (const [badID, badName] of [
    ['alpine:latest', NAME],
    ['sha256:' + 'F'.repeat(64), NAME],
    [IMAGE_ID, 'host'],
    [IMAGE_ID, 'ca-win-m4-invalid'],
  ]) assert.throws(() => dockerArgs(badID, badName));
});

test('local named-pipe Linux Desktop context and cached digest are mandatory', () => {
  validateDockerContext('desktop-linux',
    'npipe:////./pipe/dockerDesktopLinuxEngine', INFO);
  assert.deepEqual(validatePinnedImage(IMAGE),
    { imageId: IMAGE_ID, registryDigest: DIGEST });
  for (const endpoint of [
    'tcp://127.0.0.1:2375', 'ssh://server', 'unix:///var/run/docker.sock',
    'http://example.com', '',
  ]) assert.throws(() => validateDockerContext('desktop-linux', endpoint, INFO));
  for (const mutated of [
    { ...INFO, OSType: 'windows' },
    { ...INFO, OperatingSystem: 'Remote Linux' },
    { ...INFO, PidsLimit: false },
    { ...INFO, MemoryLimit: false },
  ]) assert.throws(() => validateDockerContext('desktop-linux', 'npipe://local', mutated));
  for (const image of [
    { ...IMAGE, Id: 'sha256:untagged' },
    { ...IMAGE, Os: 'windows' },
    { ...IMAGE, Architecture: 'arm64' },
    { ...IMAGE, RepoDigests: [] },
    { ...IMAGE, RepoDigests: ['random.registry/app@sha256:' + 'b'.repeat(64)] },
  ]) assert.throws(() => validatePinnedImage(image));
});

test('only exact 18 Boolean guest checks count as schema evidence', () => {
  assert.deepEqual(parseObservations(JSON.stringify(goodCases)), goodCases);
  for (const invalid of [
    '',
    'null',
    '[]',
    '{}',
    JSON.stringify({ ...goodCases, madeUpPass: true }),
    JSON.stringify({ ...goodCases, [CASES[2]]: 'true' }),
    JSON.stringify(Object.fromEntries(
      Object.entries(goodCases).filter(([key]) => key !== CASES[4]))),
    'x'.repeat(5000),
  ]) assert.throws(() => parseObservations(invalid));
  const failures = { ...goodCases, child_system_write_denied: false };
  assert.equal(parseObservations(JSON.stringify(failures)).child_system_write_denied,
    false, 'Guest failure must be observable but cannot pass qualification');
});

test('passing simulated Windows guest remains explicitly NOT product-qualified', () => {
  const mock = fakeDocker();
  const report = runFixture({
    platform: 'win32', docker: mock.docker, name: NAME, env: {},
  });
  assert.equal(report.status, 'FIXED_FIXTURE_GUEST_PASS');
  assert.equal(report.passedCases, CASES.length);
  assert.equal(report.guestRemoved, true);
  assert.equal(report.imageId, IMAGE_ID);
  assert.equal(report.registryDigest, DIGEST);
  assert.deepEqual(report.browserDiagnostics, goodBrowser);
  for (const key of [
    'modelInvoked', 'userSourceExecuted', 'sourceCodeMounted',
    'cloudModelUsed', 'untrustedChromiumSandboxQualified',
    'staticWebProfileQualified', 'generatedCodeExecutionEnabled',
  ]) assert.equal(report[key], false, key);
  assert.equal(mock.calls.filter(a => a[0] === 'run').length, 1);
  assert.equal(mock.calls.filter(a => a[0] === 'rm').length, 1);
  assert.equal(mock.calls.filter(a => a[0] === 'ps').length, 2);
  const run = mock.calls.find(a => a[0] === 'run');
  assert.equal(run.at(-2), '-', 'Guest entrypoint must read source via stdin');
  assert.equal(mock.streamedInputs.length, 1);
  const trustedSource = readFileSync(
    new URL('../tools/m4/windows_docker_guest.mjs', import.meta.url), 'utf8');
  assert.equal(mock.streamedInputs[0], trustedSource);
  assert.ok(!run.includes(trustedSource), 'No source text may appear in Windows Docker argv');
  assert.ok(mock.calls.every(a => !a.includes('pull')));
  assert.ok(mock.calls.every(a => !a.includes('build')));
});

test('malformed guest outputs, failed constraints and cleanup fail closed', () => {
  const scenarios = [
    ['denied-network-false', { cases: { ...goodCases, child_outbound_tcp_denied: false } }],
    ['unparseable', { guestOutput: 'anything' }],
    ['missing-case', { cases: { ...goodCases, git_remote_unavailable: undefined } }],
    ['guest-nonzero', { guestStatus: 2 }],
    ['docker-no-stdout', { guestStatus: 125, guestOutput: '',
      guestStderr: 'Error response from daemon: OCI runtime create failed' }],
    ['docker-spawn-failed', { guestStatus: null, guestOutput: '',
      guestErrorCode: 'ENOENT' }],
    ['guest-timeout', { timeout: true }],
    ['cleanup-unconfirmed', { cleanupOutput: 'leftover-id' }],
    ['cleanup-api-failed', { cleanupStatus: 1 }],
    ['wrong-label', { ownerLabel: 'other-owner' }],
  ];
  for (const [title, changes] of scenarios) {
    const mock = fakeDocker(changes);
    const report = runFixture({
      platform: 'win32', docker: mock.docker, name: NAME, env: {},
    });
    assert.equal(report.status, 'BLOCKED', title);
    assert.equal(report.guestRemoved,
      title.startsWith('cleanup-') || title === 'wrong-label' ? false : true);
    assert.equal(mock.calls.filter(a => a[0] === 'rm').length,
      title === 'wrong-label' ? 0 : 1);
    assert.equal(mock.calls.filter(a => a[0] === 'ps').length,
      title === 'wrong-label' ? 1 : 2);
    assert.equal(report.generatedCodeExecutionEnabled, false);
  }
});

test('remote daemon, missing image and spoofed registry digest cannot start Docker run', () => {
  const scenarios = [
    { endpoint: 'tcp://remote:2375' },
    { info: { ...INFO, OSType: 'windows' } },
    { imageStatus: 1 },
    { image: { ...IMAGE, RepoDigests: [] } },
    { collisionOutput: 'existing-container' },
    { collisionStatus: 1 },
  ];
  for (const changes of scenarios) {
    const mock = fakeDocker(changes);
    const report = runFixture({
      platform: 'win32', docker: mock.docker, name: NAME, env: {},
    });
    assert.equal(report.status, 'BLOCKED');
    assert.equal(mock.calls.filter(a => a[0] === 'run').length, 0);
    assert.equal(mock.calls.filter(a => a[0] === 'rm').length, 0);
  }
  const mock = fakeDocker();
  for (const platform of ['linux', 'darwin']) {
    const report = runFixture({ platform, docker: mock.docker, name: NAME, env: {} });
    assert.equal(report.status, 'BLOCKED');
  }
  for (const env of [{ DOCKER_HOST: 'tcp://remote' }, { DOCKER_CONTEXT: 'remote' }]) {
    const report = runFixture({ platform: 'win32', docker: mock.docker,
      name: NAME, env });
    assert.equal(report.status, 'BLOCKED');
  }
  assert.equal(mock.calls.length, 0);
});

test('no argument starts no Docker process; private evidence is bounded', () => {
  const program = spawnSync(process.execPath,
    ['tools/m4/windows_docker_qualification.mjs'], {
      encoding: 'utf8', timeout: 5000,
    });
  assert.equal(program.status, 0);
  assert.match(program.stdout, /NOT_RUN/);
  const folder = mkdtempSync(join(tmpdir(), 'ca-m4-evidence-'));
  try {
    const mock = fakeDocker({ cleanupStatus: 1 });
    const code = main(['--run'], {
      platform: 'win32', docker: mock.docker, name: NAME, env: {},
      evidenceFolder: folder,
    });
    assert.equal(code, 2);
    const files = readdirSync(folder);
    assert.equal(files.length, 1);
    const saved = JSON.parse(readFileSync(join(folder, files[0]), 'utf8'));
    assert.equal(saved.status, 'BLOCKED');
    assert.equal(saved.guestRemoved, false);
    assert.equal(saved.staticWebProfileQualified, false);
    assert.ok(!JSON.stringify(saved).includes('GITHUB_TOKEN'));
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test('streaming a small trusted source file through Node stdin preserves CLI arguments', () => {
  const smallScript = "process.stdout.write(JSON.stringify({seen:process.argv.includes('--run-fixed-guest')}))";
  const result = spawnSync(process.execPath,
    ['--input-type=module', '-', '--run-fixed-guest'], {
      input: smallScript, encoding: 'utf8', timeout: 5000, maxBuffer: 4096,
    });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { seen: true });
});

test('guest failures are classified without printing Docker stderr or credentials', () => {
  const scenarios = [
    ['failed to create task: OCI runtime create failed', 'CONTAINER_START_FAILED'],
    ['exec: "node": executable file not found', 'NODE_EXECUTABLE_UNAVAILABLE'],
    ['invalid argument: image', 'GUEST_ARGUMENT_REJECTED'],
    ['docker: Error response from daemon: invalid PID mode: private',
      'INVALID_PID_NAMESPACE_MODE'],
    ['no space left on device', 'GUEST_STORAGE_EXHAUSTED'],
    ['permission denied', 'GUEST_PERMISSION_DENIED'],
    ['Cannot connect to the Docker daemon', 'DOCKER_ENGINE_UNAVAILABLE'],
    ['Error [ERR_MODULE_NOT_FOUND]', 'GUEST_NODE_ERROR'],
  ];
  for (const [stderr, expected] of scenarios) {
    assert.equal(classifyGuestFailure({stderr}), expected);
    const mock = fakeDocker({guestStatus:125, guestOutput:'',
      guestStderr:stderr + ' SECRET_SHOULD_NEVER_APPEAR'});
    const report = runFixture({platform:'win32', docker:mock.docker,
      name:NAME, env:{}});
    assert.equal(report.status, 'BLOCKED');
    assert.equal(report.guestRemoved, true);
    assert.match(report.reason, /docker exit=125/);
    assert.ok(report.reason.includes(expected));
    assert.ok(!report.reason.includes('SECRET_SHOULD_NEVER_APPEAR'));
  }
  assert.equal(classifyGuestFailure({errorCode:'ENOENT'}), 'DOCKER_CLI_NOT_FOUND');
  assert.equal(classifyGuestFailure({timedOut:true}), 'GUEST_TIMEOUT');
});

test('a failed fixed security observation names the failing case, without qualifying execution', () => {
  const mock = fakeDocker({cases:{...goodCases, child_system_write_denied:false},
    guestStatus:2});
  const report = runFixture({platform:'win32', docker:mock.docker,
    name:NAME, env:{}});
  assert.equal(report.status,'BLOCKED');
  assert.match(report.reason,/child_system_write_denied/);
  assert.equal(report.passedCases,CASES.length-1);
  assert.equal(report.guestRemoved,true);
  assert.equal(report.generatedCodeExecutionEnabled,false);
});


test('browser paths prefer pinned headless-shell and never search the host', () => {
  const paths = browserCandidatePaths([
    'bad/../host', 'chromium-1280', 'chromium_headless_shell-1280',
    'firefox-999', 'chromium-1281',
  ]);
  assert.equal(paths[0].kind, 'HEADLESS_SHELL');
  assert.match(paths[0].path, /^\/ms-playwright\/chromium_headless_shell-1280\//);
  assert.equal(paths.filter(p => p.kind === 'HEADLESS_SHELL').length, 3);
  assert.ok(paths.some(p => p.kind === 'FULL_CHROMIUM'));
  assert.ok(paths.every(p => p.path.startsWith('/ms-playwright/')));
  assert.ok(!paths.some(p => p.path.includes('host') && !p.path.includes('headless')));
  assert.deepEqual(browserCandidatePaths(['wrong','../','firefox-22']), []);
});

test('browser failure categories are bounded and never embed Chromium stderr', () => {
  for (const [proc, expected] of [
    [{error:{code:'ENOENT'}}, 'CHROMIUM_SPAWN_ENOENT'],
    [{error:{code:'EACCES'}}, 'CHROMIUM_SPAWN_EACCES'],
    [{error:{code:'ETIMEDOUT'}}, 'CHROMIUM_TIMEOUT'],
    [{error:{code:'UNKNOWN'}}, 'CHROMIUM_SPAWN_OTHER'],
    [{stderr:'error while loading shared libraries: SECRET'}, 'CHROMIUM_SHARED_LIBRARY_ERROR'],
    [{stderr:'No usable sandbox!', signal:null}, 'CHROMIUM_SANDBOX_REJECTED'],
    [{stderr:'Resource temporarily unavailable'}, 'CHROMIUM_RESOURCE_EXHAUSTED'],
    [{signal:'SIGSYS', stderr:'SECRET'}, 'CHROMIUM_CRASHED'],
    [{status:1,stderr:'SECRET'}, 'CHROMIUM_EXIT_NONZERO'],
  ]) {
    const category = classifyBrowserFailure(proc);
    assert.equal(category, expected);
    assert.ok(BROWSER_CODES.includes(category));
    assert.ok(!category.includes('SECRET'));
  }
  assert.ok(BROWSER_KINDS.includes('HEADLESS_SHELL'));
  assert.ok(BROWSER_KINDS.includes('FULL_CHROMIUM'));
  assert.ok(BROWSER_KINDS.includes('BINARY_NOT_FOUND'));
});

test('v2 guest envelope requires exact 18 Boolean cases and allowlisted browser diagnostics', () => {
  const actual = parseGuestReport(JSON.stringify(guestEnvelope()));
  assert.deepEqual(actual.cases, goodCases);
  assert.deepEqual(actual.browser, goodBrowser);
  const cases = {...goodCases,chromium_working_fixture_executed:false};
  const browser={binary:'BINARY_NOT_FOUND',working:'BINARY_NOT_FOUND',broken:'BINARY_NOT_FOUND'};
  assert.deepEqual(parseGuestReport(JSON.stringify(guestEnvelope(cases,browser))),
    {cases,browser});
  for (const bad of [
    JSON.stringify(goodCases),
    JSON.stringify({...guestEnvelope(), extra:true}),
    JSON.stringify({...guestEnvelope(),schemaVersion:1}),
    JSON.stringify({...guestEnvelope(), browser:{...goodBrowser,working:'SECRET'}}),
    JSON.stringify({...guestEnvelope(), browser:{...goodBrowser,path:'/host/private'}}),
    JSON.stringify({...guestEnvelope(), browser:{...goodBrowser,broken:1}}),
    JSON.stringify({...guestEnvelope(), cases:{...goodCases,extra:true}}),
    JSON.stringify({blocked:true}),
    '{"unclosed":',
    'x'.repeat(7000),
  ]) assert.throws(()=>parseGuestReport(bad));
});

test('a 16/18 real-host-style result keeps both Chromium failures visible and blocks', () => {
  const observed={...goodCases,chromium_working_fixture_executed:false,
    chromium_broken_fixture_stayed_inert:false};
  const diag={binary:'HEADLESS_SHELL',working:'CHROMIUM_EXIT_NONZERO',
    broken:'CHROMIUM_EXIT_NONZERO'};
  const fake=fakeDocker({guestStatus:2,cases:observed,browser:diag});
  const report=runFixture({platform:'win32',docker:fake.docker,name:NAME,env:{}});
  assert.equal(report.status,'BLOCKED');
  assert.equal(report.passedCases,16);
  assert.equal(report.guestRemoved,true);
  assert.deepEqual(report.browserDiagnostics,diag);
  assert.match(report.reason,/chromium_working_fixture_executed/);
  assert.match(report.reason,/chromium_broken_fixture_stayed_inert/);
  assert.equal(report.staticWebProfileQualified,false);
});
