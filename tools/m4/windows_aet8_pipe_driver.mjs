// Trusted AET-8 fixed-fixture driver. No public debugging port, no generated
// source or arbitrary evaluation interface. The evaluator owns its observations.
import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';

export const QUALIFICATION_BROWSER_ARGS=Object.freeze([
  '--headless','--disable-gpu','--disable-dev-shm-usage',
  '--disable-background-networking','--disable-crash-reporter',
  '--no-first-run','--no-default-browser-check','--disable-background-mode','--remote-debugging-pipe',
]);
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function binary(){
  for(const dir of fs.readdirSync('/ms-playwright').filter(n=>/^chromium-\d+$/.test(n)).sort())
    for(const rel of ['chrome-linux64/chrome','chrome-linux/chrome']){
      const file=path.join('/ms-playwright',dir,rel);
      if(fs.existsSync(file)&&fs.statSync(file).isFile())return file;
    }
  throw Error('FULL_CHROMIUM_REQUIRED');
}
export function createPipeTransport(child){
  const outgoing=child.stdio[3],incoming=child.stdio[4];
  if(!outgoing||!incoming)throw Error('BROWSER_PIPE_MISSING');
  const pending=new Map();let buffer=Buffer.alloc(0),next=0,closed=false;
  function fail(){
    if(closed)return;closed=true;
    for(const p of pending.values()){clearTimeout(p.timer);p.reject(Error('BROWSER_PIPE_CLOSED'));}
    pending.clear();
  }
  incoming.on('data',chunk=>{
    if(closed)return;
    buffer=Buffer.concat([buffer,chunk]);
    if(buffer.length>1024*1024){fail();child.kill('SIGKILL');return;}
    let end;
    while((end=buffer.indexOf(0))>=0){
      const raw=buffer.subarray(0,end);buffer=buffer.subarray(end+1);
      let msg;try{msg=JSON.parse(raw.toString('utf8'));}catch{fail();return;}
      const p=pending.get(msg.id);if(!p)continue;
      clearTimeout(p.timer);pending.delete(msg.id);
      if(msg.error)p.reject(Error('BROWSER_PROTOCOL_ERROR'));else p.resolve(msg.result??{});
    }
  });
  incoming.on('error',fail);outgoing.on('error',fail);child.on('error',fail);child.on('exit',fail);
  return {send(method,params={},sessionId,timeoutMs=5000){
    if(closed)return Promise.reject(Error('BROWSER_PIPE_CLOSED'));
    if(pending.size>=16)return Promise.reject(Error('BROWSER_PIPE_BUSY'));
    const id=++next;
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{pending.delete(id);reject(Error('BROWSER_COMMAND_TIMEOUT'));},Math.max(1,Math.min(5000,timeoutMs)));
      pending.set(id,{resolve,reject,timer});
      outgoing.write(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})})+'\0');
    });
  },close:fail};
}
// 'close' can wait for inherited descriptors after the browser has exited.
// Bound the actual process exit, then dispose of our local pipe handles.
export function waitForBrowserExit(child,timeoutMs=3000){
  if(child.exitCode!==null||child.signalCode!==null)return Promise.resolve(true);
  return new Promise(resolve=>{
    let timer;
    const finish=value=>{clearTimeout(timer);child.removeListener('exit',onExit);child.removeListener('error',onError);resolve(value);};
    const onExit=()=>finish(true),onError=()=>finish(false);
    child.once('exit',onExit);child.once('error',onError);
    timer=setTimeout(()=>finish(false),timeoutMs);
  });
}
export async function closeBrowserProcess(child,transport,{graceMs=10000,killMs=2000}={}){
  let graceful=false,exited=false,forced=false;
  try{
    const wait=waitForBrowserExit(child,graceMs);
    try{await transport.send('Browser.close',{},undefined,1500);}catch{}
    graceful=await wait&&child.exitCode===0;
    exited=child.exitCode!==null||child.signalCode!==null;
    if(!exited){
      forced=true;
      const killed=waitForBrowserExit(child,killMs);
      child.kill('SIGKILL');exited=await killed;
    }
  }finally{
    transport.close();
    for(const stream of child.stdio??[])stream?.destroy?.();
  }
  if(!exited)throw Error('BROWSER_EXIT_TIMEOUT');
  if(forced)throw Error('BROWSER_GRACEFUL_EXIT_TIMEOUT');
  if(!graceful)throw Error(child.signalCode?'BROWSER_EXIT_SIGNAL':'BROWSER_EXIT_NONZERO');
}
async function launch(profile){
  const root=path.dirname(profile);
  const child=spawn(binary(),[...QUALIFICATION_BROWSER_ARGS,'--user-data-dir='+profile,'about:blank'],{
    env:{HOME:root,PATH:process.env.PATH,XDG_CONFIG_HOME:root+'/config',
      XDG_CACHE_HOME:root+'/cache',XDG_RUNTIME_DIR:root+'/runtime'},
    stdio:['ignore','ignore','pipe','pipe','pipe'],
  });
  child.stderr.resume();
  const transport=createPipeTransport(child);
  let closing;
  const close=()=>closing??=closeBrowserProcess(child,transport);
  try{await transport.send('Browser.getVersion');return {transport,close};}
  catch(e){try{await close();}catch{}throw e;}
}
// Only the contract's fixed locator vocabulary is supported. Candidate strings
// are data, never JS source. Real mouse and key events are emitted by CDP.
const selectorQuery=(selector,role,name)=>role?
  'Array.from(document.querySelectorAll('+JSON.stringify(role==='button'?'button':'section,[role="region"]')+')).filter(e=>(e.getAttribute("aria-label")||e.textContent.trim())==='+JSON.stringify(name)+')':
  'Array.from(document.querySelectorAll('+JSON.stringify(selector)+'))';
function makePage(transport,sessionId,url){
  const send=(method,params={},timeoutMs=5000)=>transport.send(method,params,sessionId,timeoutMs);
  const evaluate=async(expression,timeoutMs=5000)=>{
    const r=await send('Runtime.evaluate',{expression,returnByValue:true},timeoutMs);
    if(r.exceptionDetails)throw Error('FIXTURE_DOM_EVALUATION_FAILED');
    return r.result?.value;
  };
  const ready=async(deadline=Date.now()+8000)=>{
    while(Date.now()<deadline){
      if(await evaluate('location.href==='+JSON.stringify(url)+'&&document.readyState==="complete"',Math.min(1000,deadline-Date.now())))return;
      await sleep(100);
    }
    throw Error('FIXTURE_NAVIGATION_TIMEOUT');
  };
  const navigate=async()=>{const r=await send('Page.navigate',{url});if(r.errorText)throw Error('FIXTURE_NAVIGATION_FAILED');await ready();};
  const loc=(query)=>({
    async innerText(){const v=await evaluate('(()=>{const e=('+query+')[0];return e?e.innerText:null})()');if(typeof v!=='string')throw Error('MISSING_RESULT');return v;},
    async count(){return await evaluate('('+query+').length');},
    locator(selector){return loc('(()=>{const e=('+query+')[0];return e?Array.from(e.querySelectorAll('+JSON.stringify(selector)+')):[]})()');},
    getByRole(role,{name}){return loc('(()=>{const e=('+query+')[0];return e?('+selectorQuery(null,role,name)+').filter(x=>e.contains(x)):[]})()');},
    async click(){
      const point=await evaluate('(()=>{const a='+query+';if(a.length!==1)return null;const e=a[0];e.scrollIntoView({block:"center"});const r=e.getBoundingClientRect();return r.width>0&&r.height>0?{x:r.x+r.width/2,y:r.y+r.height/2}:null})()');
      if(!point||!Number.isFinite(point.x)||!Number.isFinite(point.y))throw Error('FIXTURE_CONTROL_MISSING');
      await send('Input.dispatchMouseEvent',{type:'mouseMoved',...point});
      await send('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...point});
      await send('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...point});
    },
  });
  async function key(key){
    const special={Enter:13,Backspace:8,Escape:27};
    const virtual=special[key]??key.toUpperCase().charCodeAt(0);
    const text=key.length===1?key:undefined;
    await send('Input.dispatchKeyEvent',{type:'keyDown',key,windowsVirtualKeyCode:virtual,...(text?{text,unmodifiedText:text}:{})});
    await send('Input.dispatchKeyEvent',{type:'keyUp',key,windowsVirtualKeyCode:virtual});
  }
  return {navigate,locator:selector=>loc(selectorQuery(selector)),
    getByRole:(role,{name})=>loc(selectorQuery(null,role,name)),
    keyboard:{press:key,type:async value=>{for(const k of value)await key(k);}},
    reload:async()=>{
      const previous=await evaluate('performance.timeOrigin');
      await send('Page.reload');
      const deadline=Date.now()+8000;
      while(Date.now()<deadline){
        try{if(await evaluate('performance.timeOrigin',Math.min(1000,deadline-Date.now()))!==previous){await ready(deadline);return;}}catch{}
        await sleep(100);
      }
      throw Error('BROWSER_RELOAD_TIMEOUT');
    }};
}
export async function createTrustedDriver(origin,fixture){
  if(!/^http:\/\/127\.0\.0\.1:\d+$/.test(origin)||!['working','broken','spoof'].includes(fixture))
    throw Error('FIXED_FIXTURE_REQUIRED');
  const root=fs.mkdtempSync('/tmp/ca-aet8-browser-');
  for(const d of ['profile','config','cache','runtime'])fs.mkdirSync(path.join(root,d),{mode:0o700});
  const profile=path.join(root,'profile'),url=origin+'/'+fixture+'.html';
  let browser;
  async function target(reset=false){
    browser??=await launch(profile);
    const t=browser.transport;
    let targetId;
    try{({targetId}=await t.send('Target.createTarget',{url:'about:blank'}));}catch{throw Error('BROWSER_TARGET_FAILED');}
    try{
      let sessionId;
      try{({sessionId}=await t.send('Target.attachToTarget',{targetId,flatten:true}));}catch{throw Error('BROWSER_TARGET_ATTACH_FAILED');}
      // Storage is a page-target domain; use its attached session rather
      // than the browser-level transport. Reset before opening the fixture.
      if(reset)try{await t.send('Storage.clearDataForOrigin',{origin,storageTypes:'all'},sessionId);}catch{throw Error('BROWSER_STORAGE_RESET_FAILED');}
      const page=makePage(t,sessionId,url);
      await page.navigate();return {targetId,page};
    }catch(e){try{await t.send('Target.closeTarget',{targetId});}catch{}throw e;}
  }
  async function openFresh(){
    browser??=await launch(profile);
    let active=await target(true);
    return {page:active.page,close:async()=>{if(active){await browser.transport.send('Target.closeTarget',{targetId:active.targetId});active=null;}},
      restart:async()=>{
        const previous=browser;active=null;
        // No new process is launched unless the previous one really exited 0.
        await previous.close();browser=null;
        active=await target();return active.page;
      }};
  }
  return {openFresh,close:async()=>{try{await browser?.close();}finally{fs.rmSync(root,{recursive:true,force:true});}}};
}
