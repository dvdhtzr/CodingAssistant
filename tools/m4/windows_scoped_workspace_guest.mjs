// M4.3: reviewed, fixed, disposable Windows Docker scoped-workspace fixture.
// NEVER accepts generated/user source, a model instruction, project path, or URL.
import fs from 'node:fs';
import {spawnSync} from 'node:child_process';
import net from 'node:net';
import path from 'node:path';
// The fixed guest is streamed through stdin and MUST be entirely standalone:
// no repository, host module or package is mounted into the container.
function browserCandidatePaths(dirs){
  const good=dirs.filter(x=>/^chromium(?:_headless_shell)?-\d+$/.test(x)).sort();
  const out=[];
  for(const name of good.filter(x=>x.startsWith('chromium_headless_shell-')))
    for(const rel of ['chrome-headless-shell-linux64/chrome-headless-shell',
       'chrome-linux/headless_shell'])
      out.push({path:path.posix.join('/ms-playwright',name,rel)});
  for(const name of good.filter(x=>x.startsWith('chromium-')))
    for(const rel of ['chrome-linux64/chrome','chrome-linux/chrome'])
      out.push({path:path.posix.join('/ms-playwright',name,rel)});
  return out;
}

export const FIXTURE=Object.freeze({
  seedBefore:'CA_M43_SCOPED_BEFORE\n',
  seedAfter:'CA_M43_SCOPED_AFTER\n',
  notes:'CA_M43_NOTES_UNCHANGED\n',
  outside:'CA_M43_UNMOUNTED_SENTINEL\n',
  html:'<!doctype html><html><head><meta charset="utf-8"><title>Trusted M4.3</title></head><body><button id="go">Compute</button><output id="out">0</output><script src="./app.js"></script><script>document.getElementById("go").click()</script></body></html>\n',
  js:'document.getElementById("go").addEventListener("click",()=>{document.getElementById("out").textContent="5"});\n',
  child:'CA_M43_CHILD_WRITE_OK\n',
});
export const CASES=Object.freeze([
  'non_root_process',
  'approved_seed_read',
  'approved_seed_rewrite',
  'approved_html_creation',
  'approved_js_creation',
  'approved_child_write',
  'trusted_chromium_external_js_click',
  'unmounted_sibling_read_denied',
  'unmounted_sibling_write_denied',
  'workspace_symlink_created',
  'workspace_symlink_escape_denied',
  'system_write_denied',
  'child_system_write_denied',
  'outbound_network_denied',
  'provider_secrets_absent',
  'docker_socket_absent',
  'git_metadata_absent',
]);
function denied(fn) {try{fn();return false;}catch{return true;}}
function child(source) {
  return spawnSync(process.execPath,['-e',source],{
    encoding:'utf8',timeout:5000,maxBuffer:2048,
    env:{PATH:process.env.PATH,HOME:'/tmp',XDG_CONFIG_HOME:'/tmp'},
  });
}
function netDenied(){
  return new Promise(resolve=>{
    const sock=net.createConnection({host:'203.0.113.1',port:443});
    let done=false;
    const finish=value=>{if(done)return;done=true;sock.destroy();resolve(value);};
    sock.setTimeout(1300,()=>finish(true));
    sock.once('error',()=>finish(true));
    sock.once('connect',()=>finish(false));
  });
}
function browserFileCheck(){
  try{
    const root='/ms-playwright';
    const candidates=browserCandidatePaths(fs.readdirSync(root));
    const browser=candidates.find(x=>fs.existsSync(x.path));
    if(!browser)return false;
    const result=spawnSync(browser.path,[
      '--no-sandbox', // TRUSTED fixed fixture ONLY, not generated source.
      '--headless','--disable-gpu','--disable-dev-shm-usage',
      '--disable-background-networking','--disable-crash-reporter',
      '--no-first-run','--user-data-dir=/tmp/ca-m43-chromium',
      '--dump-dom','file:///workspace/index.html',
    ],{
      encoding:'utf8',timeout:20000,maxBuffer:16384,
      env:{PATH:process.env.PATH,HOME:'/tmp',
        XDG_CACHE_HOME:'/tmp',XDG_CONFIG_HOME:'/tmp',XDG_RUNTIME_DIR:'/tmp'},
    });
    return result.status===0 &&
      result.stdout.includes('<output id="out">5</output>');
  }catch{return false;}
}
export async function runFixedWorkspaceFixture(){
  const result={};
  result.non_root_process=typeof process.getuid==='function' && process.getuid()!==0;
  result.approved_seed_read=
    fs.readFileSync('/workspace/seed.txt','utf8')===FIXTURE.seedBefore;
  if(!result.approved_seed_read)throw Error('Invalid original trusted seed');
  fs.writeFileSync('/workspace/seed.txt',FIXTURE.seedAfter,'utf8');
  result.approved_seed_rewrite=
    fs.readFileSync('/workspace/seed.txt','utf8')===FIXTURE.seedAfter;
  fs.writeFileSync('/workspace/index.html',FIXTURE.html,{flag:'wx'});
  result.approved_html_creation=
    fs.readFileSync('/workspace/index.html','utf8')===FIXTURE.html;
  fs.writeFileSync('/workspace/app.js',FIXTURE.js,{flag:'wx'});
  result.approved_js_creation=
    fs.readFileSync('/workspace/app.js','utf8')===FIXTURE.js;
  const c=child(
    "require('node:fs').writeFileSync('/workspace/child.txt',"+
    JSON.stringify(FIXTURE.child)+",{flag:'wx'});process.stdout.write('OK')"
  );
  result.approved_child_write=c.status===0 && c.stdout==='OK' &&
    fs.readFileSync('/workspace/child.txt','utf8')===FIXTURE.child;
  result.trusted_chromium_external_js_click=browserFileCheck();
  result.unmounted_sibling_read_denied=
    denied(()=>fs.readFileSync('/workspace/../outside/keep.txt'));
  result.unmounted_sibling_write_denied=
    denied(()=>fs.writeFileSync('/workspace/../outside/keep.txt','BAD'));
  try{
    fs.symlinkSync('/etc/passwd','/workspace/escape');
    result.workspace_symlink_created=fs.lstatSync('/workspace/escape').isSymbolicLink();
  }catch{result.workspace_symlink_created=false;}
  result.workspace_symlink_escape_denied=
    denied(()=>fs.writeFileSync('/workspace/escape','BAD'));
  result.system_write_denied=
    denied(()=>fs.writeFileSync('/etc/ca-m43-wrong','BAD'));
  const c2=child("try{require('node:fs').writeFileSync('/etc/ca-m43-child','BAD');"+
    "process.stdout.write('EXPOSED')}catch{process.stdout.write('DENIED')}");
  result.child_system_write_denied=c2.status===0 && c2.stdout==='DENIED';
  result.outbound_network_denied=await netDenied();
  result.provider_secrets_absent=[
    'OPENAI_API_KEY','ANTHROPIC_API_KEY','GITHUB_TOKEN','GH_TOKEN',
    'CODINGASSISTANT_OWNER_TOKEN','OLLAMA_HOST','AWS_SECRET_ACCESS_KEY',
  ].every(key=>!Object.hasOwn(process.env,key));
  result.docker_socket_absent=!fs.existsSync('/var/run/docker.sock') &&
    !fs.existsSync('/run/podman/podman.sock');
  result.git_metadata_absent=!fs.existsSync('/workspace/.git');
  return result;
}
if(process.argv.includes('--run-fixed-workspace')){
  try{
    const results=await runFixedWorkspaceFixture();
    if(Object.keys(results).length!==CASES.length ||
       !CASES.every(k=>typeof results[k]==='boolean'))
      throw Error('Invalid test schema');
    process.stdout.write(JSON.stringify(results)+'\n');
    if(!CASES.every(k=>results[k]))process.exitCode=2;
  }catch{
    process.stdout.write(JSON.stringify({blocked:true})+'\n');
    process.exitCode=2;
  }
}
