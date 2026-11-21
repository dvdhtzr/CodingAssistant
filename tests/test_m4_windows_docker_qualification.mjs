import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  IMAGE_TAG, dockerArgs, parseObservations, validateDockerContext,
  validatePinnedImage, runFixture, writePrivateEvidence, main,
} from '../tools/m4/windows_docker_qualification.mjs';
import { CASES } from '../tools/m4/windows_docker_guest.mjs';

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
function result(status, stdout = '') { return { status, stdout }; }
function fakeDocker(overrides = {}) {
  const calls = [];
  function docker(args) {
    calls.push(args);
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
        JSON.stringify(overrides.cases ?? goodCases)),
      timedOut: overrides.timeout ?? false,
    };
    if (args[0] === 'rm') return result(0);
    if (args[0] === 'ps') return result(overrides.cleanupStatus ?? 0,
      overrides.cleanupOutput ?? '');
    throw new Error('Unexpected fake Docker operation: ' + args.join(' '));
  }
  return { docker, calls };
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
    '--pid=private', '--init', '--entrypoint=node',
    '--input-type=module', '--run-fixed-guest',
  ]) assert.ok(args.includes(must), 'Missing Docker guard ' + must);
  for (const mustNot of [
    '--volume', '-v', '--mount', '--privileged', '--network=host',
    '--publish', '-p', '--device', '--env-file', '--cap-add',
    '--security-opt=seccomp=unconfined', '--user=0',
  ]) assert.ok(!args.includes(mustNot), 'Unauthorized Docker flag ' + mustNot);
  assert.equal(args.filter(x => x === '--run-fixed-guest').length, 1);
  assert.equal(args[args.indexOf('--entrypoint=node') + 1], IMAGE_ID);
  assert.ok(args.includes('--tmpfs=/tmp:rw,nosuid,nodev,size=256m'));
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
  for (const key of [
    'modelInvoked', 'userSourceExecuted', 'sourceCodeMounted',
    'cloudModelUsed', 'untrustedChromiumSandboxQualified',
    'staticWebProfileQualified', 'generatedCodeExecutionEnabled',
  ]) assert.equal(report[key], false, key);
  assert.equal(mock.calls.filter(a => a[0] === 'run').length, 1);
  assert.equal(mock.calls.filter(a => a[0] === 'rm').length, 1);
  assert.equal(mock.calls.filter(a => a[0] === 'ps').length, 1);
  const run = mock.calls.find(a => a[0] === 'run');
  assert.equal(run.at(-3), '--', 'Guest entrypoint must not have extra executable calls');
  assert.ok(mock.calls.every(a => !a.includes('pull')));
  assert.ok(mock.calls.every(a => !a.includes('build')));
});

test('malformed guest outputs, failed constraints and cleanup fail closed', () => {
  const scenarios = [
    ['denied-network-false', { cases: { ...goodCases, child_outbound_tcp_denied: false } }],
    ['unparseable', { guestOutput: 'anything' }],
    ['missing-case', { cases: { ...goodCases, git_remote_unavailable: undefined } }],
    ['guest-nonzero', { guestStatus: 2 }],
    ['guest-timeout', { timeout: true }],
    ['cleanup-unconfirmed', { cleanupOutput: 'leftover-id' }],
    ['cleanup-api-failed', { cleanupStatus: 1 }],
  ];
  for (const [title, changes] of scenarios) {
    const mock = fakeDocker(changes);
    const report = runFixture({
      platform: 'win32', docker: mock.docker, name: NAME, env: {},
    });
    assert.equal(report.status, 'BLOCKED', title);
    assert.equal(report.guestRemoved, title.startsWith('cleanup-') ? false : true);
    assert.equal(mock.calls.filter(a => a[0] === 'rm').length, 1);
    assert.equal(mock.calls.filter(a => a[0] === 'ps').length, 1);
    assert.equal(report.generatedCodeExecutionEnabled, false);
  }
});

test('remote daemon, missing image and spoofed registry digest cannot start Docker run', () => {
  const scenarios = [
    { endpoint: 'tcp://remote:2375' },
    { info: { ...INFO, OSType: 'windows' } },
    { imageStatus: 1 },
    { image: { ...IMAGE, RepoDigests: [] } },
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
