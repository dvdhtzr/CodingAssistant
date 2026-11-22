// Owner-invoked generated Static Web ZIP smoke guest.
// Intentional risk boundary: Chrome --no-sandbox INSIDE disposable hardened
// Docker ONLY. Never run directly on host or claim complete verification.
import fs from 'node:fs';
import net from 'node:net';
import http from 'node:http';
import path from 'node:path';
import {spawn,spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';

const BASE64='__CA_UNTRUSTED_SNAPSHOT_BASE64__';
const CASES=Object.freeze([
  'exact_source_hashes','non_root','no_host_mounts',
  'root_write_denied','network_denied','javascript_syntax',
  'local_assets_linked','browser_rendered','source_integrity_unchanged',
]);
function sha(buf){return createHash('sha256').update(buf).digest('hex');}
function safe(p){return typeof p==='string'&&p.length<=140&&
  /^(?:[A-Za-z0-9_-]+\/){0,3}[A-Za-z0-9_-]+\.(?:html|css|js|json)$/.test(p)&&
  !p.split('/').some(x=>['node_modules','bin','obj','artifacts']
    .includes(x.toLowerCase()));}
function denied(fn){try{fn();return false;}catch{return true;}}
async function netDenied(){
  return await new Promise(resolve=>{
    const socket=net.createConnection({host:'203.0.113.1',port:443});
    let done=false;
    function end(v){if(done)return;done=true;socket.destroy();resolve(v);}
    socket.once('error',()=>end(true));
    socket.once('connect',()=>end(false));
    socket.setTimeout(1000,()=>end(true));
  });
}
function browserBinary(){
  try{
    const names=fs.readdirSync('/ms-playwright')
      .filter(x=>/^chromium(?:_headless_shell)?-\d+$/.test(x)).sort();
    for(const name of [...names.filter(x=>x.startsWith('chromium_headless_shell-')),
      ...names.filter(x=>x.startsWith('chromium-'))]){
      const rel=name.startsWith('chromium_headless_shell-')?
        ['chrome-headless-shell-linux64/chrome-headless-shell','chrome-linux/headless_shell']:
        ['chrome-linux64/chrome','chrome-linux/chrome'];
      for(const item of rel){
        const full=path.posix.join('/ms-playwright',name,item);
        if(fs.existsSync(full))return full;
      }
    }
  }catch{}
  return null;
}
function checkJs(text){
  const child=spawnSync(process.execPath,['--check','-'],{
    input:text,encoding:'utf8',timeout:4000,maxBuffer:2048,
    env:{HOME:'/tmp',PATH:process.env.PATH},
  });
  return child.status===0;
}
function linkedAssets(files){
  const refs=new Set();
  for(const [name,entry] of files){
    if(!name.endsWith('.html'))continue;
    const html=entry.content;
    const tokens=[...html.matchAll(/<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["']/gi),
      ...html.matchAll(/<link\b[^>]*\bhref\s*=\s*["']([^"']+)["']/gi)];
    for(const token of tokens){
      const raw=token[1].split(/[?#]/,1)[0];
      if(!raw||/^https?:|^\/\//i.test(raw))return false;
      const normalized=path.posix.normalize(
        path.posix.join(path.posix.dirname(name),raw.replace(/^\/+/,'')));
      if(!safe(normalized)||!files.has(normalized))return false;
      refs.add(normalized.toLowerCase());
    }
  }
  for(const name of files.keys())
    if(name.endsWith('.js')&&!refs.has(name.toLowerCase()))
      return false; // catches generated but disconnected app.js
  return true;
}
async function browserRender(files){
  const executable=browserBinary();
  if(!executable)return false;
  const contentTypes={html:'text/html; charset=utf-8',
    js:'application/javascript; charset=utf-8',
    css:'text/css; charset=utf-8',json:'application/json; charset=utf-8'};
  const harness='<!doctype html><html><body><pre id="ca-smoke">PENDING</pre><script>'+
    '(async()=>{try{const frame=document.createElement("iframe");'+
    'frame.src="/index.html";'+
    'const loaded=new Promise((yes,no)=>{const t=setTimeout(()=>no("timeout"),5000);'+
    'frame.onload=()=>{clearTimeout(t);yes()}});document.body.append(frame);'+
    'await loaded;await new Promise(r=>setTimeout(r,150));'+
    'const doc=frame.contentDocument;'+
    'const rendered=!!doc?.body && doc.body.textContent.trim().length>0;'+
    'document.querySelector("#ca-smoke").textContent='+
    'rendered?"CA_SOURCE_SMOKE_RENDERED":"CA_SOURCE_SMOKE_EMPTY";'+
    '}catch{document.querySelector("#ca-smoke").textContent="CA_SOURCE_SMOKE_BLOCKED"}})();'+
    '</script></body></html>';
  const server=http.createServer((req,res)=>{
    let url;
    try{url=new URL(req.url,'http://localhost');}
    catch{res.writeHead(404);res.end();return;}
    const name=decodeURIComponent(url.pathname).replace(/^\/+/,'');
    if(name==='__ca_smoke__.html'){
      res.writeHead(200,{'Content-Type':contentTypes.html,
        'Cache-Control':'no-store'});res.end(harness);return;
    }
    if(!safe(name)||!files.has(name)){
      res.writeHead(404);res.end();return;
    }
    const ext=path.posix.extname(name).slice(1);
    res.writeHead(200,{'Content-Type':contentTypes[ext],
      'X-Content-Type-Options':'nosniff','Cache-Control':'no-store'});
    res.end(files.get(name).content);
  });
  await new Promise((resolve,reject)=>{
    server.once('error',reject);server.listen(0,'127.0.0.1',resolve);
  });
  try{
    const uri='http://127.0.0.1:'+server.address().port+'/__ca_smoke__.html';
    const chromium=await new Promise(resolve=>{
      const child=spawn(executable,[
        '--no-sandbox', // Only inside this isolated no-mount Docker guest.
        '--headless','--disable-gpu','--disable-dev-shm-usage',
        '--disable-background-networking','--disable-crash-reporter',
        '--no-first-run','--no-default-browser-check',
        '--user-data-dir=/tmp/ca-smoke-profile','--virtual-time-budget=10000',
        '--dump-dom',uri,
      ],{env:{HOME:'/tmp',PATH:process.env.PATH,
        XDG_CONFIG_HOME:'/tmp',XDG_CACHE_HOME:'/tmp',
        XDG_RUNTIME_DIR:'/tmp'},stdio:['ignore','pipe','pipe']});
      let output='',finished=false,overflow=false;
      const timer=setTimeout(()=>{overflow=true;child.kill('SIGKILL');},25000);
      child.stdout.on('data',data=>{
        if(output.length+data.length>32768){overflow=true;child.kill('SIGKILL');}
        else output+=String(data);
      });
      child.stderr.on('data',()=>{}); // No user output or browser logs retained.
      function done(ok){
        if(finished)return;finished=true;clearTimeout(timer);
        resolve(ok&&!overflow&&output.includes('CA_SOURCE_SMOKE_RENDERED'));
      }
      child.once('error',()=>done(false));
      child.once('close',code=>done(code===0));
    });
    return chromium;
  }finally{server.closeAllConnections();server.close();}
}
export async function smoke(){
  const checks={};
  const decoded=Buffer.from(BASE64,'base64');
  if(decoded.length<20||decoded.length>240000)throw Error('Source bounds');
  const payload=JSON.parse(decoded.toString('utf8'));
  if(!payload||payload.schemaVersion!==1||!Array.isArray(payload.files)||
     payload.files.length<1||payload.files.length>12)throw Error('Source protocol');
  const files=new Map(),names=new Set();
  let size=0;
  for(const entry of payload.files){
    if(!entry||!safe(entry.path)||typeof entry.content!=='string'||
       typeof entry.sha256!=='string'||!(/^[a-f0-9]{64}$/.test(entry.sha256))||
       names.has(entry.path.toLowerCase()))throw Error('Unsafe file');
    names.add(entry.path.toLowerCase());
    const data=Buffer.from(entry.content,'utf8');
    size+=data.length;
    if(data.length>65536||data.length<1||sha(data)!==entry.sha256)
      throw Error('Changed source');
    files.set(entry.path,{content:entry.content,sha256:entry.sha256});
  }
  if(size>160000||!files.has('index.html'))throw Error('Source size or entry');
  checks.exact_source_hashes=true;
  checks.non_root=process.getuid?.()===1000;
  checks.no_host_mounts=!fs.existsSync('/workspace') &&
    !fs.existsSync('/host')&&!fs.existsSync('/var/run/docker.sock');
  checks.root_write_denied=denied(()=>
    fs.writeFileSync('/etc/ca-smoke-escape','not allowed'));
  checks.network_denied=await netDenied();
  checks.javascript_syntax=[...files].filter(([name])=>name.endsWith('.js'))
    .every(([,entry])=>checkJs(entry.content));
  checks.local_assets_linked=linkedAssets(files);
  checks.browser_rendered=checks.javascript_syntax && checks.local_assets_linked &&
    await browserRender(files);
  checks.source_integrity_unchanged=[...files.values()].every(e=>
    sha(Buffer.from(e.content,'utf8'))===e.sha256);
  return {schemaVersion:1,checks,browserSandbox:'DISABLED_CONTAINER_ONLY'};
}
if(process.argv.includes('--run-owner-smoke')){
  try{
    const result=await smoke();
    process.stdout.write(JSON.stringify(result)+'\n');
    if(!Object.values(result.checks).every(Boolean))process.exitCode=2;
  }catch{
    process.stdout.write(JSON.stringify({blocked:true})+'\n');
    process.exitCode=2;
  }
}
