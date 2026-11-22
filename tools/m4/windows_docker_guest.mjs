// M4.2: TRUSTED, FIXED Windows Docker Desktop qualification guest.
// NOT a general evaluator, NOT a model-supplied script, NOT an execution API.
// Uses only Node standard library plus Chromium already cached in an
// independently reviewed Playwright container image. No external downloads.
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

export const CASES = Object.freeze([
  'valid_javascript_parsed',
  'invalid_javascript_rejected',
  'syntax_side_effect_not_executed',
  'chromium_working_fixture_executed',
  'chromium_broken_fixture_stayed_inert',
  'non_root_user',
  'system_write_denied',
  'symlink_write_denied',
  'traversal_write_denied',
  'system_credentials_read_denied',
  'provider_secrets_absent',
  'container_socket_absent',
  'windows_host_paths_unavailable',
  'outbound_tcp_denied',
  'host_ollama_loopback_denied',
  'child_outbound_tcp_denied',
  'child_system_write_denied',
  'git_remote_unavailable',
]);

const DENIED = 'DENIED';
const EXPOSED = 'EXPOSED';
function denied(operation) {
  try { operation(); return false; } catch { return true; }
}
function fixedChild(args, options = {}) {
  return spawnSync(process.execPath, args, {
    encoding: 'utf8', timeout: 4500, maxBuffer: 4096,
    env: { PATH: process.env.PATH, HOME: '/tmp' }, ...options,
  });
}
function childDenied(source) {
  const result = fixedChild(['-e', source]);
  return result.status === 0 && result.stdout.trim() === DENIED;
}
function cannotConnect(host, port) {
  return new Promise(resolve => {
    const socket = net.createConnection({ host, port });
    let done = false;
    function finish(blocked) {
      if (done) return;
      done = true;
      socket.destroy();
      resolve(blocked);
    }
    socket.setTimeout(1600, () => finish(true));
    socket.once('error', () => finish(true));
    socket.once('connect', () => finish(false));
  });
}

// Official Playwright images contain a dedicated headless-shell binary.
// Prefer it for --dump-dom; fall back to full Chromium only if it is not
// installed. Never search PATH or the Windows host.
export const BROWSER_KINDS = Object.freeze([
  'HEADLESS_SHELL', 'FULL_CHROMIUM', 'BINARY_NOT_FOUND', 'DISCOVERY_FAILED',
]);
export const BROWSER_CODES = Object.freeze([
  'PASS', 'BINARY_NOT_FOUND', 'DISCOVERY_FAILED',
  'CHROMIUM_SPAWN_ENOENT', 'CHROMIUM_SPAWN_EACCES',
  'CHROMIUM_SPAWN_OTHER', 'CHROMIUM_TIMEOUT',
  'CHROMIUM_SHARED_LIBRARY_ERROR', 'CHROMIUM_SANDBOX_REJECTED',
  'CHROMIUM_RESOURCE_EXHAUSTED', 'CHROMIUM_CRASHED',
  'CHROMIUM_EXIT_NONZERO', 'CHROMIUM_DOM_MISSING_MARKER',
  'CHROMIUM_WORKING_HANDLER_FAILED', 'CHROMIUM_NEGATIVE_FIXTURE_FAILED',
]);

export function browserCandidatePaths(dirs) {
  const revisions = [...dirs].filter(name =>
    /^chromium(?:_headless_shell)?-\d+$/.test(name)).sort();
  const result = [];
  // Headless shell is the preferred binary for automated headless tests.
  for (const name of revisions.filter(x => x.startsWith('chromium_headless_shell-')))
    for (const relative of [
      'chrome-headless-shell-linux64/chrome-headless-shell',
      'chrome-linux/headless_shell', 'chrome-linux/headless-shell',
    ]) result.push({kind:'HEADLESS_SHELL', path:path.posix.join('/ms-playwright',name,relative)});
  for (const name of revisions.filter(x => x.startsWith('chromium-')))
    for (const relative of ['chrome-linux64/chrome', 'chrome-linux/chrome'])
      result.push({kind:'FULL_CHROMIUM', path:path.posix.join('/ms-playwright',name,relative)});
  return result;
}

function browserExecutable() {
  try {
    if (!fs.existsSync('/ms-playwright'))
      return {executable:null,kind:'BINARY_NOT_FOUND'};
    for (const item of browserCandidatePaths(fs.readdirSync('/ms-playwright')))
      if (fs.existsSync(item.path) && fs.statSync(item.path).isFile())
        return {executable:item.path,kind:item.kind};
    return {executable:null,kind:'BINARY_NOT_FOUND'};
  } catch {
    return {executable:null,kind:'DISCOVERY_FAILED'};
  }
}

// Exit diagnostics are strictly allowlisted, not raw Chromium stderr.
// Never emit paths, environment values or browser profiles.
export function classifyBrowserFailure(proc) {
  if (proc.error?.code === 'ETIMEDOUT') return 'CHROMIUM_TIMEOUT';
  if (proc.error?.code === 'ENOENT') return 'CHROMIUM_SPAWN_ENOENT';
  if (proc.error?.code === 'EACCES') return 'CHROMIUM_SPAWN_EACCES';
  if (proc.error) return 'CHROMIUM_SPAWN_OTHER';
  const message=String(proc.stderr||'').slice(0,8192);
  if (/error while loading shared libraries|cannot open shared object file/i.test(message))
    return 'CHROMIUM_SHARED_LIBRARY_ERROR';
  if (/no usable sandbox|sandbox.*failed|sandbox.*denied/i.test(message))
    return 'CHROMIUM_SANDBOX_REJECTED';
  if (/resource temporarily unavailable|no space left on device|out of memory/i.test(message))
    return 'CHROMIUM_RESOURCE_EXHAUSTED';
  if (proc.signal) return 'CHROMIUM_CRASHED';
  return 'CHROMIUM_EXIT_NONZERO';
}

function runBrowserCase(browser, good) {
  if (!browser.executable)
    return {passed:false,code:browser.kind};
  const marker = good ? 'CA_GUEST_REFERENCE_OK' : 'CA_GUEST_BROKEN_INERT';
  const script = good
    ? "document.querySelector('[data-n]').addEventListener('click',()=>{document.body.dataset.acceptance='5'})"
    : '/* intentionally unlinked application JS as in #107 */';
  const html = '<!doctype html><html><head><meta charset="utf-8"></head><body>' +
    '<button type="button" data-n>Calculate</button>' +
    '<script>' + script + '</script>' +
    '<script>document.querySelector("button").click();' +
    (good ? 'document.body.dataset.marker="CA_GUEST_REFERENCE_OK";' :
      'document.body.dataset.marker="CA_GUEST_BROKEN_INERT";') +
    '</script></body></html>';
  const file = '/tmp/ca-m4-' + (good ? 'working' : 'broken') + '.html';
  fs.writeFileSync(file, html, { flag: 'wx', mode: 0o600 });
  const proc = spawnSync(browser.executable, [
    '--no-sandbox', // Trusted fixed fixture ONLY; never untrusted source.
    '--headless', '--disable-gpu', '--disable-dev-shm-usage',
    '--disable-background-networking', '--no-first-run',
    '--disable-crash-reporter', '--no-default-browser-check',
    '--user-data-dir=/tmp/ca-m4-browser-' + (good ? 'good' : 'bad'),
    '--dump-dom', 'file://' + file,
  ], {
    encoding: 'utf8', timeout: 20000, maxBuffer: 16 * 1024,
    env: {
      PATH: process.env.PATH, HOME: '/tmp', XDG_CONFIG_HOME: '/tmp/config',
      XDG_CACHE_HOME: '/tmp/cache', XDG_RUNTIME_DIR: '/tmp',
    },
  });
  if (proc.status !== 0)
    return {passed:false,code:classifyBrowserFailure(proc)};
  if (!proc.stdout.includes(marker))
    return {passed:false,code:'CHROMIUM_DOM_MISSING_MARKER'};
  if (good && !proc.stdout.includes('data-acceptance="5"'))
    return {passed:false,code:'CHROMIUM_WORKING_HANDLER_FAILED'};
  if (!good && proc.stdout.includes('data-acceptance="5"'))
    return {passed:false,code:'CHROMIUM_NEGATIVE_FIXTURE_FAILED'};
  return {passed:true,code:'PASS'};
}

export async function fixedFixtureChecks() {
  const checks = {};
  const writeOnSyntax = "require('node:fs').writeFileSync('/tmp/ca-m4-should-not-exist','BAD')";
  checks.valid_javascript_parsed = fixedChild(['--check', '-'], {
    input: writeOnSyntax,
  }).status === 0;
  checks.invalid_javascript_rejected = fixedChild(['--check', '-'], {
    input: 'function invalid( {',
  }).status !== 0;
  checks.syntax_side_effect_not_executed =
    !fs.existsSync('/tmp/ca-m4-should-not-exist');
  const browser = browserExecutable();
  const workingBrowser = runBrowserCase(browser, true);
  const brokenBrowser = runBrowserCase(browser, false);
  checks.chromium_working_fixture_executed = workingBrowser.passed;
  checks.chromium_broken_fixture_stayed_inert = brokenBrowser.passed;
  // These are stable, allowlisted categories, not command output.
  const browserDiagnostics = {
    binary: browser.kind, working: workingBrowser.code, broken: brokenBrowser.code,
  };
  checks.non_root_user = typeof process.getuid === 'function' && process.getuid() !== 0;
  checks.system_write_denied =
    denied(() => fs.writeFileSync('/etc/ca-m4-write', 'BAD'));
  fs.symlinkSync('/etc/passwd', '/tmp/ca-m4-escape');
  checks.symlink_write_denied =
    denied(() => fs.writeFileSync('/tmp/ca-m4-escape', 'BAD'));
  checks.traversal_write_denied =
    denied(() => fs.writeFileSync('/tmp/../etc/ca-m4-traversal', 'BAD'));
  checks.system_credentials_read_denied =
    denied(() => fs.readFileSync('/etc/shadow'));
  const secretNames = [
    'OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GITHUB_TOKEN', 'GH_TOKEN',
    'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AZURE_OPENAI_API_KEY',
    'OLLAMA_HOST', 'OPENCLAW_GATEWAY_TOKEN', 'CODINGASSISTANT_OWNER_TOKEN',
  ];
  checks.provider_secrets_absent = secretNames.every(key =>
    !Object.hasOwn(process.env, key));
  checks.container_socket_absent = [
    '/var/run/docker.sock', '/run/docker.sock',
    '/run/podman/podman.sock',
  ].every(item => !fs.existsSync(item));
  checks.windows_host_paths_unavailable = [
    '/host_mnt/c/Users', '/run/desktop/mnt/host/c/Users', '/mnt/c/Users',
  ].every(item => !fs.existsSync(item));
  checks.outbound_tcp_denied = await cannotConnect('203.0.113.1', 443);
  checks.host_ollama_loopback_denied =
    await cannotConnect('127.0.0.1', 11434);
  checks.child_outbound_tcp_denied = childDenied([
    "const s=require('node:net').createConnection({host:'203.0.113.1',port:443});",
    "let done=false;const finish=(v)=>{if(done)return;done=true;console.log(v);s.destroy()};",
    "s.setTimeout(1200,()=>finish('DENIED'));",
    "s.on('error',()=>finish('DENIED'));s.on('connect',()=>finish('EXPOSED'));",
  ].join(''));
  checks.child_system_write_denied = childDenied([
    "try{require('node:fs').writeFileSync('/etc/ca-m4-child','BAD');",
    "console.log('EXPOSED')}catch{console.log('DENIED')}",
  ].join(''));
  const remote = spawnSync('git', [
    '-c', 'credential.helper=', 'ls-remote',
    'https://example.invalid/ca-m4-remote-escape-probe',
  ], {
    encoding: 'utf8', timeout: 4500, maxBuffer: 512,
    env: { PATH: process.env.PATH, HOME: '/tmp', GIT_TERMINAL_PROMPT: '0' },
  });
  checks.git_remote_unavailable =
    (remote.error?.code === 'ENOENT') ||
    (remote.status !== null && remote.status !== 0);
  return {checks,browserDiagnostics};
}

if (process.argv.includes('--run-fixed-guest')) {
  try {
    const result = await fixedFixtureChecks();
    if (Object.keys(result.checks).length !== CASES.length ||
        !BROWSER_KINDS.includes(result.browserDiagnostics.binary) ||
        !BROWSER_CODES.includes(result.browserDiagnostics.working) ||
        !BROWSER_CODES.includes(result.browserDiagnostics.broken))
      throw new Error('Invalid fixed diagnostic schema');
    // Host independently verifies exact case and diagnostic schemas.
    process.stdout.write(JSON.stringify({
      schemaVersion:2, cases:result.checks, browser:result.browserDiagnostics,
    }) + '\n');
    if (!CASES.every(key => result.checks[key] === true)) process.exitCode = 2;
  } catch {
    process.stdout.write(JSON.stringify({ blocked: true }) + '\n');
    process.exitCode = 2;
  }
}
