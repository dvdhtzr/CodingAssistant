// Trusted AET-8 fixed-fixture driver. No public debugging port, no generated
// source or arbitrary evaluation interface. The evaluator owns its observations.
import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';

export const QUALIFICATION_BROWSER_ARGS=Object.freeze([
  '--headless','--disable-gpu','--disable-dev-shm-usage',
  '--disable-background-networking','--disable-crash-reporter',
  '--no-first-run','--no-default-browser-check','--remote-debugging-pipe',
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
  return {send(method,params={},sessionId){
    if(closed)return Promise.reject(Error('BROWSER_PIPE_CLOSED'));
    if(pending.size>=16)return Promise.reject(Error('BROWSER_PIPE_BUSY'));
    const id=++next;
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{pending.delete(id);reject(Error('BROWSER_COMMAND_TIMEOUT'));},5000);
      pending.set(id,{resolve,reject,timer});
      outgoing.write(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})})+'\0');
    });
  },close:fail};
}
function processClosed(child){
  if(child.exitCode!==null||child.signalCode!==null)return Promise.resolve();
  return new Promise(resolve=>{
    const timer=setTimeout(()=>{child.kill('SIGKILL');},2500);
    child.once('close',()=>{clearTimeout(timer);resolve();});
  });
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
  let done=false;
  const close=async()=>{
    if(done)return;done=true;
    const wait=processClosed(child);
    try{await transport.send('Browser.close');}catch{}
    await wait;transport.close();
  };
  try{await transport.send('Browser.getVersion');return {transport,close};}
  catch(e){await close();throw e;}
}
// Only the contract's fixed locator vocabulary is supported. Candidate strings
// are data, never JS source. Real mouse and key events are emitted by CDP.
const selectorQuery=(selector,role,name)=>role?
  'Array.from(document.querySelectorAll('+JSON.stringify(role==='button'?'button':'section,[role="region"]')+')).filter(e=>(e.getAttribute("aria-label")||e.textContent.trim())==='+JSON.stringify(name)+')':
  'Array.from(document.querySelectorAll('+JSON.stringify(selector)+'))';
function makePage(transport,sessionId,url){
  const send=(method,params={})=>transport.send(method,params,sessionId);
  const evaluate=async expression=>{
    const r=await send('Runtime.evaluate',{expression,returnByValue:true});
    if(r.exceptionDetails)throw Error('FIXTURE_DOM_EVALUATION_FAILED');
    return r.result?.value;
  };
  const ready=async()=>{
    for(let i=0;i<40;i++){
      if(await evaluate('location.href==='+JSON.stringify(url)+'&&document.readyState==="complete"'))return;
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
      for(let i=0;i<40;i++){
        try{if(await evaluate('performance.timeOrigin')!==previous){await ready();return;}}catch{}
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
        await browser.close();browser=null;active=null;
        active=await target();return active.page;
      }};
  }
  return {openFresh,close:async()=>{try{await browser?.close();}finally{fs.rmSync(root,{recursive:true,force:true});}}};
}
