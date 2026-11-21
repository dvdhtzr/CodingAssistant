// M4.2: TRUSTRD, FIXED Windows Docker Desktop qualification guest.
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

// Browser binary layout differs between Playwright Chromium and headless shell
// releases. Search only inside the fixed image's known browser directory,
// never the Windows host or an arbitrary PATH executable.
function browserExecutable() {
  const root = '/ms-playwright';
  if (!fs.existsSync(root)) return null;
  const subdirs = fs.readdirSync(root).filter(dir =>
    /^chromium(?:_headless_shell)?-\d+$/.test(dir));
  for (const dir of subdirs) {
    for (const relative of [
      'chrome-linux64/chrome',
      'chrome-linux/chrome',
      'chrome-headless-shell-linux64/chrome-headless-shell',
      'chrome-linux/headless_shell',
      'chrome-linux/headless-shell',
    ]) {
      const candidate = path.join(root, dir, relative);
      if (fs.existsSync(candidate) && fs.statSync(candidate).isFile())
        return candidate;
    }
  }
  return null;
}

function runBrowserCase(executable, good) {
  if (!executable) return false;
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
  const proc = spawnSync(executable, [
    '--no-sandbox', // Trusted fixed fixture ONLY; no untrusted browsing.
    '--headless', '--disable-gpu', '--disable-dev-shm-usage',
    '--disable-background-networking', '--no-first-run',
    '--no-default-browser-check', '--no-zygote',
    '--user-data-dir=/tmp/ca-m4-browser-' + (good ? 'good' : 'bad'),
    '--dump-dom', 'file://' + file,
  ], {
    encoding: 'utf8', timeout: 20000, maxBuffer: 16 * 1024,
    env: {
      PATH: process.env.PATH, HOME: '/tmp', XDG_CONFIG_HOME: '/tmp/config',
      XDG_CACHE_HOME: '/tmp/cache', XDG_RUNTIME_DIR: '/tmp',
    },
  });
  if (proc.status !== 0 || !proc.stdout.includes(marker)) return false;
  return good ? proc.stdout.includes('data-acceptance="5"') :
    !proc.stdout.includes('data-acceptance="5"');
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
  const executable = browserExecutable();
  checks.chromium_working_fixture_executed = runBrowserCase(executable, true);
  checks.chromium_broken_fixture_stayed_inert = runBrowserCase(executable, false);
  checks.non_root_user = process.getuid?.() !== 0;
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
  return checks;
}

if (process.argv.includes('--run-fixed-guest')) {
  try {
    const result = await fixedFixtureChecks();
    // Host accepts EXACTLY these Boolean keys with every result true.
    if (Object.keys(result).length !== CASES.length) throw new Error('wrong result count');
    process.stdout.write(JSON.stringify(result) + '\n');
    if (!CASES.every(key => result[key] === true)) process.exitCode = 2;
  } catch {
    process.stdout.write(JSON.stringify({ blocked: true }) + '\n');
    process.exitCode = 2;
  }
}
