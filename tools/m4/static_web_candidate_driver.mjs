// Candidate-only Chromium driver. No CLI, dispatcher or executable admission.
// Only a separately qualified isolated guest may instantiate the real driver.
// The existing trusted fixture driver and its qualified bytes remain intact.
import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {QUALIFICATION_BROWSER_ARGS,createPipeTransport,closeBrowserProcess}
  from './windows_aet8_pipe_driver.mjs';

const fail=code=>{throw Error(code);};
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function candidateUrl(origin){
  if(typeof origin!=='string'||!/^http:\/\/127\.0\.0\.1:[0-9]{1,5}$/.test(origin)||/[\r\n]/.test(origin))
    fail('CANDIDATE_ORIGIN_INVALID');
  let parsed;try{parsed=new URL(origin);}catch{fail('CANDIDATE_ORIGIN_INVALID');}
  if(!parsed.port||Number(parsed.port)<1||Number(parsed.port)>65535||parsed.origin!==origin)
    fail('CANDIDATE_ORIGIN_INVALID');
  return origin+'/index.html';
}
const buttonNames=new Set(['2','3','Add','Equals','Clear','Back recall','History',
  '8 + 2 = 10','2 + 3 = 5']);
function roleQuery(role,name){
  if(role==='region'&&name==='Calculation history')
    return 'Array.from(document.querySelectorAll("section,[role=region]")).filter(e=>(e.getAttribute("aria-label")||e.textContent.trim())==='+JSON.stringify(name)+')';
  if(role==='button'&&buttonNames.has(name))
    return 'Array.from(document.querySelectorAll("button")).filter(e=>(e.getAttribute("aria-label")||e.textContent.trim())==='+JSON.stringify(name)+')';
  fail('CANDIDATE_LOCATOR_NOT_REVIEWED');
}
// CDP is private to this driver. The application cannot supply expressions,
// selectors, arbitrary URLs, or protocol commands. DOM reads execute in a
// separate world rather than using the page's possibly modified JS builtins.
export function createCandidatePage(transport,sessionId,origin,assertActive=()=>{}){
  const url=candidateUrl(origin);
  const send=(method,params={},timeout=5000)=>{
    assertActive();return transport.send(method,params,sessionId,timeout);
  };
  async function evaluate(expression,timeout=5000,requireReady=true){
    const tree=await send('Page.getFrameTree',{},timeout);
    const frame=tree.frameTree?.frame;
    if(typeof frame?.id!=='string'||frame.url!==url)fail('CANDIDATE_NAVIGATION_CHANGED');
    const isolated=await send('Page.createIsolatedWorld',{frameId:frame.id,
      worldName:'ca-candidate-independent-v1'},timeout);
    if(!Number.isSafeInteger(isolated.executionContextId))fail('CANDIDATE_ISOLATED_WORLD_UNAVAILABLE');
    const code='(()=>{if(location.href!=='+JSON.stringify(url)+
      (requireReady?'||document.readyState!=="complete"':'')+
      ')throw Error("UNEXPECTED_DOCUMENT");return ('+expression+');})()';
    const reply=await send('Runtime.evaluate',{expression:code,returnByValue:true,
      contextId:isolated.executionContextId},timeout);
    if(reply.exceptionDetails)fail('CANDIDATE_DOM_OBSERVATION_FAILED');
    return reply.result?.value;
  }
  async function ready(deadline){
    while(Date.now()<deadline){
      try{if(await evaluate('document.readyState==="complete"',1000,false)===true)return;}
      catch(e){
        if(!['CANDIDATE_NAVIGATION_CHANGED','CANDIDATE_DOM_OBSERVATION_FAILED',
          'BROWSER_COMMAND_TIMEOUT','BROWSER_TIMEOUT_RUNTIME_EVALUATE','BROWSER_PROTOCOL_ERROR'].includes(e?.message))throw e;
      }
      await sleep(100);
    }
    fail('CANDIDATE_NAVIGATION_TIMEOUT');
  }
  const loc=query=>Object.freeze({
    async innerText(){
      const value=await evaluate('(()=>{const a='+query+';return a.length===1?a[0].innerText:null})()');
      if(typeof value!=='string')fail('MISSING_RESULT');
      if(value.length>4096)fail('CANDIDATE_DOM_OBSERVATION_FAILED');
      return value;
    },
    async count(){
      const n=await evaluate('('+query+').length');
      if(!Number.isSafeInteger(n)||n<0||n>100000)fail('CANDIDATE_DOM_OBSERVATION_FAILED');
      return n;
    },
    locator(selector){
      if(selector!=='li')fail('CANDIDATE_LOCATOR_NOT_REVIEWED');
      return loc('(()=>{const a='+query+';return a.length===1?Array.from(a[0].querySelectorAll("li")):[]})()');
    },
    getByRole(role,{name,exact}={}){
      if(exact!==true)fail('CANDIDATE_LOCATOR_NOT_REVIEWED');
      return loc('(()=>{const a='+query+';return a.length===1?('+roleQuery(role,name)+').filter(x=>a[0].contains(x)):[]})()');
    },
    async click(){
      const p=await evaluate('(()=>{const a='+query+';if(a.length!==1)return null;const e=a[0];e.scrollIntoView({block:"center"});const r=e.getBoundingClientRect();return r.width>0&&r.height>0?{x:r.x+r.width/2,y:r.y+r.height/2}:null})()');
      if(p===null)fail('FIXTURE_CONTROL_MISSING');
      if(!Number.isFinite(p?.x)||!Number.isFinite(p?.y)||Math.abs(p.x)>100000||Math.abs(p.y)>100000)
        fail('CANDIDATE_DOM_OBSERVATION_FAILED');
      await send('Input.dispatchMouseEvent',{type:'mouseMoved',x:p.x,y:p.y});
      for(const type of ['mousePressed','mouseReleased'])
        await send('Input.dispatchMouseEvent',{type,button:'left',clickCount:1,x:p.x,y:p.y});
      await evaluate('true');
    }
  });
  async function key(key){
    if(typeof key!=='string'||!['Enter','Backspace','Escape'].includes(key)&&(key.length!==1||!/^[0-9.+*/-]$/.test(key)))
      fail('CANDIDATE_KEY_NOT_REVIEWED');
    await evaluate('true');
    const virtual={Enter:13,Backspace:8,Escape:27}[key]??key.toUpperCase().charCodeAt(0);
    const text=key.length===1?key:undefined;
    await send('Input.dispatchKeyEvent',{type:'keyDown',key,windowsVirtualKeyCode:virtual,...(text?{text,unmodifiedText:text}:{})});
    await send('Input.dispatchKeyEvent',{type:'keyUp',key,windowsVirtualKeyCode:virtual});
    await evaluate('true');
  }
  const page=Object.freeze({
    locator(selector){
      if(selector!=='output#display')fail('CANDIDATE_LOCATOR_NOT_REVIEWED');
      return loc('Array.from(document.querySelectorAll("output#display"))');
    },
    getByRole(role,{name,exact}={}){
      if(exact!==true)fail('CANDIDATE_LOCATOR_NOT_REVIEWED');
      return loc(roleQuery(role,name));
    },
    keyboard:Object.freeze({press:key,type:async value=>{
      if(typeof value!=='string'||value.length>40||/[^0-9.+*/-]/.test(value)||value.length===0)
        fail('CANDIDATE_KEY_NOT_REVIEWED');
      for(const k of value)await key(k);
    }}),
    reload:async()=>{
      const previous=await evaluate('performance.timeOrigin');
      if(!Number.isFinite(previous))fail('CANDIDATE_RELOAD_FAILED');
      await send('Page.reload',{ignoreCache:true});
      const deadline=Date.now()+10000;
      while(Date.now()<deadline){
        await ready(deadline);
        try{
          const current=await evaluate('performance.timeOrigin');
          if(Number.isFinite(current)&&current!==previous)return;
        }catch(e){if(!['CANDIDATE_DOM_OBSERVATION_FAILED','BROWSER_PROTOCOL_ERROR','CANDIDATE_NAVIGATION_CHANGED'].includes(e?.message))throw e;}
        await sleep(100);
      }
      fail('CANDIDATE_RELOAD_FAILED');
    }
  });
  return Object.freeze({page,async navigate(){
    const reply=await send('Page.navigate',{url});
    if(reply.errorText)fail('CANDIDATE_NAVIGATION_FAILED');
    await ready(Date.now()+15000);
  }});
}

// Pure lifecycle seam with host-owned operations for model-free tests.
// Each mandatory case uses a new profile and process. Only restart within
// that case preserves storage; any failed shutdown poisons the session.
export function createCandidateSessionManager(origin,{createProfile,launch,removeProfile}){
  candidateUrl(origin);
  let current=null,closed=false,poisoned=false,busy=false;
  const requireIdle=()=>{
    if(closed||poisoned)fail('CANDIDATE_DRIVER_BLOCKED');
    if(busy||current)fail('CANDIDATE_SESSION_BUSY');
  };
  async function stop(state){
    const browser=state.browser;
    state.browser=null;state.generation++;
    if(browser)try{await browser.close();}catch(e){state.shutdownUnconfirmed=true;poisoned=true;throw e;}
  }
  async function dispose(state){
    try{
      await stop(state);
      if(state.shutdownUnconfirmed)fail('CANDIDATE_BROWSER_CLEANUP_UNCONFIRMED');
      await removeProfile(state.profile);
    }
    catch(e){poisoned=true;throw e;}
    finally{if(current===state)current=null;}
  }
  async function start(state){
    const generation=++state.generation;
    const assertActive=()=>{
      if(current!==state||state.generation!==generation||closed||poisoned)
        fail('CANDIDATE_SESSION_STALE');
    };
    try{state.browser=await launch(state.profile,origin,assertActive);}
    catch(e){state.shutdownUnconfirmed=true;throw e;}
    await state.browser.navigate();
    return state.browser.page;
  }
  return Object.freeze({
    async openFresh(){
      requireIdle();busy=true;
      let state;
      try{
        const profile=await createProfile();
        state={profile,browser:null,generation:0,shutdownUnconfirmed:false};
        current=state;
        const page=await start(state);
        let ended=false,restarted=false;
        return Object.freeze({page,
          async restart(){
            if(ended||restarted||busy||current!==state||poisoned||closed)
              fail('CANDIDATE_SESSION_STALE');
            restarted=true;busy=true;
            try{await stop(state);return await start(state);}
            catch(e){poisoned=true;throw e;}
            finally{busy=false;}
          },
          async close(){
            if(ended)return;
            if(busy)fail('CANDIDATE_SESSION_BUSY');
            ended=true;busy=true;
            try{await dispose(state);}finally{busy=false;}
          }});
      }catch(e){
        poisoned=true;
        if(state)try{await dispose(state);}catch{}
        throw e;
      }finally{busy=false;}
    },
    async close(){
      if(busy)fail('CANDIDATE_SESSION_BUSY');
      if(closed){if(poisoned)fail('CANDIDATE_DRIVER_BLOCKED');return;}
      closed=true;
      if(current)await dispose(current);
      if(poisoned)fail('CANDIDATE_DRIVER_BLOCKED');
    }
  });
}
function fullChromium(){
  for(const dir of fs.readdirSync('/ms-playwright').filter(n=>/^chromium-\d+$/.test(n)).sort())
    for(const relative of ['chrome-linux64/chrome','chrome-linux/chrome']){
      const file=path.join('/ms-playwright',dir,relative);
      if(fs.existsSync(file)&&fs.statSync(file).isFile())return file;
    }
  fail('FULL_CHROMIUM_REQUIRED');
}
// Real transport is deliberately not wired to any host runner or CLI.
export function createCandidateDriver(origin){
  candidateUrl(origin);
  if(process.platform!=='linux'||process.getuid?.()!==1000)
    fail('CANDIDATE_GUEST_REQUIRED');
  return createCandidateSessionManager(origin,{
    createProfile(){
      const root=fs.mkdtempSync('/tmp/ca-candidate-browser-');
      for(const name of ['profile','config','cache','runtime'])
        fs.mkdirSync(path.join(root,name),{mode:0o700});
      return root;
    },
    removeProfile:root=>fs.rmSync(root,{recursive:true,force:true}),
    async launch(root,origin,assertActive){
      const child=spawn(fullChromium(),[...QUALIFICATION_BROWSER_ARGS,'--user-data-dir='+path.join(root,'profile'),'about:blank'],{
        env:{HOME:root,PATH:process.env.PATH,XDG_CONFIG_HOME:root+'/config',
          XDG_CACHE_HOME:root+'/cache',XDG_RUNTIME_DIR:root+'/runtime'},
        stdio:['ignore','ignore','pipe','pipe','pipe']
      });
      child.stderr.resume();
      const transport=createPipeTransport(child);
      let closing;
      const close=()=>closing??=closeBrowserProcess(child,transport);
      try{
        await transport.send('Browser.getVersion');
        const {targetId}=await transport.send('Target.createTarget',{url:'about:blank'});
        const {sessionId}=await transport.send('Target.attachToTarget',{targetId,flatten:true});
        if(typeof sessionId!=='string')fail('CANDIDATE_BROWSER_ATTACH_FAILED');
        const adapter=createCandidatePage(transport,sessionId,origin,assertActive);
        return {...adapter,close};
      }catch(e){await close();throw e;}
    }
  });
}
