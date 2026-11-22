#!/usr/bin/env node
// M4.2 Windows Docker Desktop fixed-fixture host qualification.
// Never dispatch arbitrary source, never pull/build images, never enable
// CodingAssistant's task executor. The guest payload is checked-in code.
import { spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CASES } from './windows_docker_guest.mjs';

export const IMAGE_TAG = 'mcr.microsoft.com/playwright:v1.65.1-noble-amd64';
const GUEST_SOURCE = fs.readFileSync(
  new URL('./windows_docker_guest.mjs', import.meta.url), 'utf8');
const SHA = /^sha256:[0-9a-f]{64}$/;
const NAME = /^ca-win-m4-[0-9a-f]{12}$/;
const EXPECTED_OS = 'win32';

function fail(message) {
  throw new Error(message);
}

export function dockerArgs(imageId, name) {
  if (!SHA.test(imageId) || !NAME.test(name))
    fail('Invalid pinned image ID or disposable guest name');
  return [
    'run', '--rm', '-i', '--pull=never', '--name', name,
    '--label', 'codingassistant.m4=' + name,
    '--network=none', '--read-only', '--cap-drop=all',
    '--security-opt=no-new-privileges', '--user=1000:1000',
    '--pids-limit=96', '--memory=2g', '--cpus=2',
    '--ipc=private', '--pid=private',
    '--tmpfs=/tmp:rw,nosuid,nodev,mode=1777,size=256m',
    '--shm-size=256m', '--init',
    '--env=HOME=/tmp', '--env=XDG_CONFIG_HOME=/tmp/config',
    '--env=XDG_CACHE_HOME=/tmp/cache',
    '--entrypoint=node', imageId,
    '--input-type=module', '-', '--run-fixed-guest',
  ];
}

export function parseObservations(raw) {
  if (typeof raw !== 'string' || raw.length < 2 || raw.length > 4000)
    fail('Guest observations absent or oversized');
  const parsed = JSON.parse(raw.trim());
  if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object' ||
      Object.keys(parsed).length !== CASES.length ||
      !CASES.every(key => Object.hasOwn(parsed, key) &&
        typeof parsed[key] === 'boolean'))
    fail('Guest observations do not have the exact Boolean case schema');
  return Object.fromEntries(CASES.map(key => [key, parsed[key]]));
}

function parseJsonResponse(response, label) {
  if (response.status !== 0 || typeof response.stdout !== 'string')
    fail(label + ' returned a nonzero status');
  try { return JSON.parse(response.stdout.trim()); }
  catch { fail(label + ' returned invalid JSON'); }
}

export function validateDockerContext(contextName, endpoint, info) {
  if (!contextName || !/^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,70}$/.test(contextName))
    fail('Docker context name was invalid');
  if (typeof endpoint !== 'string' ||
      !endpoint.startsWith('npipe://'))
    fail('Remote/TCP/SSH/Unix Docker engine contexts are forbidden');
  if (info?.OSType !== 'linux' ||
      !String(info.OperatingSystem).includes('Docker Desktop') ||
      info.PidsLimit !== true || info.MemoryLimit !== true)
    fail('Local Docker Desktop Linux engine with resource limits is required');
}

export function validatePinnedImage(metadata) {
  if (!metadata || !SHA.test(metadata.Id) ||
      metadata.Os !== 'linux' || metadata.Architecture !== 'amd64' ||
      !Array.isArray(metadata.RepoDigests) ||
      !metadata.RepoDigests.some(digest =>
        /^mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64}$/.test(digest)))
    fail('Offline Playwright amd64 image has no acceptable local ID and registry digest');
  return {
    imageId: metadata.Id,
    registryDigest: metadata.RepoDigests.find(digest =>
      /^mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64}$/.test(digest)),
  };
}

// Do not persist or echo raw Docker stderr (which may contain host details).
// Emit a bounded, allowlisted category and numeric exit code instead.
export function classifyGuestFailure(response) {
  if (response?.timedOut) return 'GUEST_TIMEOUT';
  if (response?.errorCode === 'ENOENT') return 'DOCKER_CLI_NOT_FOUND';
  const message = String(response?.stderr || '').slice(0, 8192);
  if (/no such image|unable to find image|image.*not found/i.test(message))
    return 'CACHED_IMAGE_UNAVAILABLE';
  if (/no space left on device/i.test(message)) return 'GUEST_STORAGE_EXHAUSTED';
  if (/executable file not found|exec:.*node.*not found/i.test(message))
    return 'NODE_EXECUTABLE_UNAVAILABLE';
  if (/permission denied|access is denied/i.test(message))
    return 'GUEST_PERMISSION_DENIED';
  if (/bad option|unknown flag|invalid argument|invalid option/i.test(message))
    return 'GUEST_ARGUMENT_REJECTED';
  if (/OCI runtime|containerd|failed to create task|failed to start/i.test(message))
    return 'CONTAINER_START_FAILED';
  if (/failed to connect|docker daemon|dockerDesktopLinuxEngine/i.test(message))
    return 'DOCKER_ENGINE_UNAVAILABLE';
  if (/SyntaxError|ERR_\w+/i.test(message)) return 'GUEST_NODE_ERROR';
  return 'UNCLASSIFIED_GUEST_FAILURE';
}

export function shellDocker(args, timeout = 8000, input = undefined) {
  const result = spawnSync('docker', args, {
    encoding: 'utf8', timeout, maxBuffer: 64 * 1024,
    windowsHide: true, shell: false, input,
  });
  return {
    status: result.status,
    stdout: result.stdout || '',
    stderr: result.stderr || '',
    errorCode: result.error?.code ?? null,
    timedOut: result.error?.code === 'ETIMEDOUT',
  };
}

// The entire model-free orchestration is injectable for Windows/Linux CI.
// Default OS/platform and command runner still fail closed in production.
export function runFixture({
  platform = process.platform, docker = shellDocker, env = process.env,
  name = 'ca-win-m4-' + randomBytes(6).toString('hex'),
} = {}) {
  const report = {
    schemaVersion: 1,
    stage: 'M4.2-windows-docker-desktop-fixed-fixture',
    status: 'BLOCKED',
    recordedAtUtc: new Date().toISOString(),
    host: platform, imageId: null, imageTag: IMAGE_TAG,
    registryDigest: null,
    guestPayloadSha256: createHash('sha256').update(GUEST_SOURCE).digest('hex'),
    cases: null, passedCases: 0, requiredCases: CASES.length,
    guestRemoved: false,
    modelInvoked: false, userSourceExecuted: false,
    sourceCodeMounted: false, cloudModelUsed: false,
    untrustedChromiumSandboxQualified: false,
    staticWebProfileQualified: false,
    generatedCodeExecutionEnabled: false,
    reason: null,
  };
  if (platform !== EXPECTED_OS) {
    report.reason = 'Windows host only; no Docker run attempted';
    return report;
  }
  if (!NAME.test(name)) {
    report.reason = 'Invalid disposable container name';
    return report;
  }
  if (env.DOCKER_HOST || env.DOCKER_CONTEXT) {
    report.reason = 'Docker environment overrides are not accepted';
    return report;
  }
  let attempted = false;
  try {
    const current = docker(['context', 'show']);
    if (current.status !== 0) fail('Cannot determine Docker Desktop context');
    const contextName = current.stdout.trim();
    const context = parseJsonResponse(docker(['context', 'inspect',
      contextName, '--format', '{{json .}}']), 'Docker context');
    const info = parseJsonResponse(docker(['info', '--format', '{{json .}}']),
      'Docker info');
    validateDockerContext(contextName, context?.Endpoints?.docker?.Host, info);
    const imageInfo = parseJsonResponse(docker(['image', 'inspect', IMAGE_TAG,
      '--format', '{{json .}}']), 'Cached Playwright image');
    const pin = validatePinnedImage(imageInfo);
    report.imageId = pin.imageId;
    report.registryDigest = pin.registryDigest;
    // Refuse an existing container with the same random name.
    const collision = docker(['ps', '-a', '--filter', 'name=^/' + name + '$',
      '--format', '{{.ID}}']);
    if (collision.status !== 0 || collision.stdout.trim() !== '')
      fail('Disposable Docker name already exists or preflight failed');

    // No remote pull; the observed immutable local image ID is required.
    attempted = true;
    // Stream only our checked-in, SHA-recorded trusted payload to Node via
    // Docker stdin. Windows CLI no longer has to quote 8 KB of JS as -e.
    const guest = docker(dockerArgs(pin.imageId, name), 95000, GUEST_SOURCE);
    if (guest.timedOut) fail('Disposable guest timed out');
    const guestExit = Number.isInteger(guest.status) ? guest.status : 'unavailable';
    if (!guest.stdout || !guest.stdout.trim()) {
      fail('No guest observations; docker exit=' + guestExit +
        '; category=' + classifyGuestFailure(guest));
    }
    const observations = parseObservations(guest.stdout);
    report.cases = observations;
    report.passedCases = CASES.filter(key => observations[key]).length;
    if (report.passedCases !== CASES.length)
      fail('Guest checks failed: ' +
        CASES.filter(key => !observations[key]).join(', '));
    if (guest.status !== 0)
      fail('Guest returned all-true observations but exited nonzero; category=' +
        classifyGuestFailure(guest));
    report.status = 'FIXED_FIXTURE_GUEST_PASS';
  } catch (error) {
    report.reason = String(error.message || error).slice(0, 240);
  } finally {
    if (attempted) {
      try {
        // Never remove a container without verifying our exact owner label.
        const ownership = docker(['container', 'inspect', name,
          '--format', '{{json .Config.Labels}}'], 15000);
        if (ownership.status === 0) {
          const labels = JSON.parse(ownership.stdout.trim());
          if (!labels || labels['codingassistant.m4'] !== name)
            fail('Container ownership label mismatch; refusing to remove');
          docker(['rm', '-f', name], 15000);
        }
        const remaining = docker(['ps', '-a',
          '--filter', 'name=^/' + name + '$',
          '--format', '{{.ID}}'], 15000);
        report.guestRemoved = remaining.status === 0 &&
          remaining.stdout.trim() === '';
      } catch {
        report.guestRemoved = false;
      }
      if (!report.guestRemoved) {
        report.status = 'BLOCKED';
        report.reason = 'Disposable container cleanup not independently confirmed';
      }
    }
  }
  return report;
}

export function writePrivateEvidence(report, folder) {
  const root = folder ??
    path.join(process.env.LOCALAPPDATA || os.homedir(),
      'CodingAssistant', 'qualifications');
  fs.mkdirSync(root, { recursive: true, mode: 0o700 });
  const dest = path.join(root,
    'windows-static-web-' + randomBytes(6).toString('hex') + '.json');
  fs.writeFileSync(dest, JSON.stringify(report, null, 2) + '\n',
    { encoding: 'utf8', mode: 0o600, flag: 'wx' });
  return dest;
}

export function main(argv = process.argv.slice(2), options = {}) {
  if (argv.length === 0) {
    process.stdout.write('WINDOWS_STATIC_WEB_DOCKER: NOT_RUN\n');
    process.stdout.write('Use --run for the explicit, fixed-fixture local Docker qualification.\n');
    process.stdout.write('No model-generated source or execution permission is enabled.\n');
    return 0;
  }
  if (argv.length !== 1 || argv[0] !== '--run') {
    process.stderr.write('Usage: node tools/m4/windows_docker_qualification.mjs [--run]\n');
    return 2;
  }
  const report = runFixture(options);
  const evidence = writePrivateEvidence(report, options.evidenceFolder);
  process.stdout.write('M4.2 WINDOWS_DOCKER_' + report.status + '\n');
  process.stdout.write('  fixed guest cases: ' +
    report.passedCases + '/' + report.requiredCases + '\n');
  process.stdout.write('  container absent after run: ' + report.guestRemoved + '\n');
  process.stdout.write('  generated code and untrusted browser: NOT_AUTHORIZED\n');
  process.stdout.write('  Static Web runtime profile: UNQUALIFIED\n');
  if (report.reason) process.stdout.write('  blocker: ' + report.reason + '\n');
  process.stdout.write('Evidence: ' + evidence + '\n');
  return report.status === 'FIXED_FIXTURE_GUEST_PASS' &&
    report.guestRemoved ? 0 : 2;
}

if (process.argv[1] && path.resolve(process.argv[1]) ===
    fileURLToPath(import.meta.url))
  process.exitCode = main();
