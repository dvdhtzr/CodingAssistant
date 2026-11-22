// M4.4 fixed, independent Calculation Journal evaluator guest.
// TRUSTED checked-in fixtures ONLY. Never run generated/user applications.
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import net from 'node:net';
import {spawn,spawnSync} from 'node:child_process';

// Host substitutes EXACT reviewed harness text when streaming this source.
// This placeholder without host substitution can never produce a passing test.
const HARNESS_SOURCE="PLACEHOLDER_M44_HARNESS_SOURCE";
export const FILES=Object.freeze([
  'broken.html','broken.js','working.html','working.js',
]);
export const CASES=Object.freeze([
  'positive_javascript_syntax',
  'positive_cross_file_link',
  'negative_unlinked_application',
  'negative_inert_browser',
  'mouse_2_plus_3_equals_5',
  'keyboard_12_5_divided_by_2',
  'decimal_0_1_plus_0_2',
  'backspace_and_divide_by_zero',
  'back_and_history_recall',
  'history_survives_page_reload',
  'history_survives_browser_restart',
  'snapshot_read_only',
  'neighbor_not_mounted',
  'child_system_write_denied',
  'outbound_network_denied',
  'non_root_user',
]);

function browserExecutable(){
  try{
    const names=fs.readdirSync('/ms-playwright')
      .filter(n=>/^chromium(?:_headless_shell)?-\d+$/.test(n)).sort();
    const paths=[];
    for(const name of names.filter(n=>n.startsWith('chromium_headless_shell-')))
      for(const rel of ['chrome-headless-shell-linux64/chrome-headless-shell',
        'chrome-linux/headless_shell'])
        paths.push(path.posix.join('/ms-playwright',name,rel));
    for(const name of names.filter(n=>n.startsWith('chromium-')))
      for(const rel of ['chrome-linux64/chrome','chrome-linux/chrome'])
        paths.push(path.posix.join('/ms-playwright',name,rel));
    return paths.find(p=>fs.existsSync(p))??null;
  }catch{return null;}
}
function harness(stage){
  return '<!doctype html><html><head><meta charset="utf-8"></head>'+
    '<body data-stage="'+stage+'">'+
    '<pre id="ca-m44-result">PENDING</pre>'+
    '<script src="/harness.js"></script></body></html>';
}
function syntax(source){
  const r=spawnSync(process.execPath,['--check','-'],{
    input:source,encoding:'utf8',timeout:4500,maxBuffer:4096,
    env:{PATH:process.env.PATH,HOME:'/tmp'},
  });
  return r.status===0;
}
function denyNetwork(){
  return new Promise(resolve=>{
    const socket=net.createConnection({host:'203.0.113.1',port:443});
    let completed=false;
    const finish=x=>{if(completed)return;completed=true;socket.destroy();resolve(x);};
    socket.setTimeout(1300,()=>finish(true));
    socket.once('error',()=>finish(true));
    socket.once('connect',()=>finish(false));
  });
}
function launchBrowser(executable,url){
  return new Promise(resolve=>{
    const child=spawn(executable,[
      '--no-sandbox', // Frozen trusted fixtures only; NOT generated source.
      '--headless','--disable-gpu','--disable-dev-shm-usage',
      '--disable-background-networking','--disable-crash-reporter',
      '--no-first-run','--no-default-browser-check',
      '--user-data-dir=/tmp/ca-m44-profile',
      '--virtual-time-budget=15000','--dump-dom',url,
    ],{
      env:{PATH:process.env.PATH,HOME:'/tmp',XDG_CONFIG_HOME:'/tmp',
        XDG_CACHE_HOME:'/tmp',XDG_RUNTIME_DIR:'/tmp'},
      stdio:['ignore','pipe','pipe'],
    });
    let stdout='',ended=false,oversized=false;
    const timer=setTimeout(()=>{oversized=true;child.kill('SIGKILL');},30000);
    child.stdout.on('data',chunk=>{
      if(stdout.length+chunk.length>60000){
        oversized=true;child.kill('SIGKILL');
      }else stdout+=chunk.toString('utf8');
    });
    child.stderr.on('data',()=>{}); // Never return raw browser logs.
    function finish(code){
      if(ended)return;
      ended=true;clearTimeout(timer);
      if(code!==0||oversized){resolve(null);return;}
      const match=stdout.match(/<pre id="ca-m44-result">([^<]{1,2500})<\/pre>/);
      if(!match){resolve(null);return;}
      try{resolve(JSON.parse(match[1]));}catch{resolve(null);}
    }
    child.once('error',()=>finish(null));
    child.once('close',code=>finish(code));
  });
}

export async function runFixedEvaluation(){
  const tests={};
  const documents=Object.fromEntries(FILES.map(name=>[
    name,fs.readFileSync(path.posix.join('/workspace',name),'utf8'),
  ]));
  tests.positive_javascript_syntax=syntax(documents['working.js']);
  tests.positive_cross_file_link=
    /<script\s+src=["']\.\/working\.js["']\s+defer><\/script>/i
      .test(documents['working.html']) &&
    documents['working.html'].includes('id="display"');
  tests.negative_unlinked_application=
    !/<script[^>]+src=["'][^"']*broken\.js/i.test(documents['broken.html']) &&
    documents['broken.html'].includes('TODO connect calculator handlers');
  tests.snapshot_read_only=(()=>{
    try{fs.writeFileSync('/workspace/ca-m44-escape','BAD');return false;}
    catch{return true;}
  })();
  tests.neighbor_not_mounted=!fs.existsSync('/workspace/../outside/keep.txt');
  const child=spawnSync(process.execPath,['-e',
    "try{require('node:fs').writeFileSync('/etc/ca-m44-child','BAD');"+
    "process.stdout.write('EXPOSED')}catch{process.stdout.write('DENIED')}",
  ],{
    encoding:'utf8',timeout:4000,maxBuffer:512,
    env:{PATH:process.env.PATH,HOME:'/tmp'},
  });
  tests.child_system_write_denied=child.status===0 && child.stdout==='DENIED';
  tests.outbound_network_denied=await denyNetwork();
  tests.non_root_user=typeof process.getuid==='function' && process.getuid()!==0;

  const allowed=new Set([...FILES,'harness-first.html','harness-restart.html','harness.js']);
  const server=http.createServer((req,res)=>{
    try{
      const pathname=new URL(req.url,'http://127.0.0.1').pathname.slice(1);
      if(!allowed.has(pathname)){
        res.writeHead(404,{'Cache-Control':'no-store'});res.end();return;
      }
      const body=pathname==='harness-first.html'?harness('first'):
        pathname==='harness-restart.html'?harness('restart'):
        pathname==='harness.js'?HARNESS_SOURCE:documents[pathname];
      const type=pathname.endsWith('.js')?'text/javascript; charset=utf-8':
        'text/html; charset=utf-8';
      res.writeHead(200,{'Content-Type':type,'Cache-Control':'no-store',
        'X-Content-Type-Options':'nosniff'});
      res.end(body);
    }catch{res.writeHead(404);res.end();}
  });
  await new Promise((resolve,reject)=>{
    server.once('error',reject);
    server.listen(0,'127.0.0.1',resolve);
  });
  try{
    const bin=browserExecutable();
    if(bin){
      const port=server.address().port;
      const base='http://127.0.0.1:'+port+'/';
      const first=await launchBrowser(bin,base+'harness-first.html');
      if(first?.stage==='first' && first.tests &&
         typeof first.tests==='object' && !Array.isArray(first.tests))
        for(const key of [
          'negative_inert_browser','mouse_2_plus_3_equals_5',
          'keyboard_12_5_divided_by_2','decimal_0_1_plus_0_2',
          'backspace_and_divide_by_zero','back_and_history_recall',
          'history_survives_page_reload',
        ]) tests[key]=first.tests[key]===true;
      if(first?.tests?.history_survives_page_reload===true){
        // Two independently launched Chromium processes, one tmp profile.
        const second=await launchBrowser(bin,base+'harness-restart.html');
        tests.history_survives_browser_restart=
          second?.stage==='restart' &&
          second?.tests?.history_survives_browser_restart===true;
      }
    }
  }finally{
    server.closeAllConnections();
    server.close();
  }
  return Object.fromEntries(CASES.map(key=>[key,tests[key]===true]));
}

if(process.argv.includes('--run-trusted-evaluator')){
  try{
    const tests=await runFixedEvaluation();
    if(Object.keys(tests).length!==CASES.length ||
      !CASES.every(k=>typeof tests[k]==='boolean'))
      throw Error('Bad case schema');
    process.stdout.write(JSON.stringify(tests)+'\n');
    if(!CASES.every(k=>tests[k]))process.exitCode=2;
  }catch{
    process.stdout.write(JSON.stringify({blocked:true})+'\n');
    process.exitCode=2;
  }
}
