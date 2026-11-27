// Owner-local qualification journal, not an executable grant. Exclusive
// active directory serializes the pilot; completed attempts are retained.
// A damaged or ambiguous journal blocks rather than being silently replaced.
import fs from 'node:fs';
import path from 'node:path';
const fail=code=>{throw Error(code);};
const hex=(x,n)=>typeof x==='string'&&x.length===n&&/^[a-f0-9]+$/.test(x);
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const LABEL='codingassistant.static-web-pilot-qualification';
const stages=['Reserved','Creating','Created','Audited','Starting','Exited','CleanupPending','Cleaned'];
const transitions={
  Reserved:['Creating','CleanupPending'],Creating:['Created','CleanupPending'],
  Created:['Audited','CleanupPending'],Audited:['Starting','CleanupPending'],
  Starting:['Exited','CleanupPending'],Exited:['CleanupPending'],
  CleanupPending:['CleanupPending','Cleaned'],Cleaned:[]
};
function regular(file,directory=false){
  const s=fs.lstatSync(file);
  if(s.isSymbolicLink()||(directory?!s.isDirectory():!s.isFile())||
    path.resolve(fs.realpathSync(file)).toLowerCase()!==path.resolve(file).toLowerCase())
    fail('PILOT_JOURNAL_PATH_INVALID');
  return s;
}
function rootPath(root){
  if(typeof root!=='string'||!path.isAbsolute(root))fail('PILOT_JOURNAL_PATH_INVALID');
  fs.mkdirSync(root,{recursive:true,mode:0o700});regular(root,true);
  return path.resolve(root);
}
function identityValid(x){
  return exact(x,['attemptId','name','label','policySha256','imageId','engineId','candidateSha256','archiveSha256'])&&
    hex(x.attemptId,32)&&x.name==='ca-win-pilot-'+x.attemptId&&x.label===LABEL&&
    hex(x.policySha256,64)&&typeof x.imageId==='string'&&x.imageId.startsWith('sha256:')&&hex(x.imageId.slice(7),64)&&
    typeof x.engineId==='string'&&x.engineId.length>0&&x.engineId.length<=128&&/^[A-Za-z0-9:_-]+$/.test(x.engineId)&&
    hex(x.candidateSha256,64)&&hex(x.archiveSha256,64);
}
function writeDurable(file,value,flag){
  const fd=fs.openSync(file,flag,0o600);
  try{fs.writeFileSync(fd,JSON.stringify(value)+'\n','utf8');fs.fsyncSync(fd);}finally{fs.closeSync(fd);}
}
function readBounded(file,max){
  const s=regular(file);
  if(s.size<2||s.size>max)fail('PILOT_JOURNAL_INVALID');
  const raw=fs.readFileSync(file,'utf8');
  if(!raw.endsWith('\n'))fail('PILOT_JOURNAL_INVALID');
  return raw;
}
function checkEvent(prev,event){
  if(!exact(event,['sequence','stage','containerId'])||event.sequence!==(prev?prev.sequence+1:0)||
    !stages.includes(event.stage)||!(event.containerId===null||hex(event.containerId,64)))
    fail('PILOT_JOURNAL_INVALID');
  if(!prev){
    if(event.stage!=='Reserved'||event.containerId!==null)fail('PILOT_JOURNAL_INVALID');
  }else{
    if(!transitions[prev.stage].includes(event.stage)||
      prev.containerId!==null&&event.containerId!==prev.containerId||
      ['Created','Audited','Starting','Exited'].includes(event.stage)&&event.containerId===null||
      event.stage==='Creating'&&event.containerId!==null)fail('PILOT_JOURNAL_TRANSITION_INVALID');
  }
}
function load(root){
  const active=path.join(root,'active');regular(active,true);
  try{
    const header=JSON.parse(readBounded(path.join(active,'identity.json'),8192));
    if(!exact(header,['schemaVersion','ownerPid','identity'])||header.schemaVersion!==1||
      !Number.isSafeInteger(header.ownerPid)||header.ownerPid<1||!identityValid(header.identity))
      fail('PILOT_JOURNAL_INVALID');
    const events=readBounded(path.join(active,'events.jsonl'),65536).trimEnd().split('\n').map(x=>JSON.parse(x));
    let prev=null;for(const event of events){checkEvent(prev,event);prev=event;}
    if(!prev)fail('PILOT_JOURNAL_INVALID');
    return {active,header,events,last:prev};
  }catch{fail('PILOT_JOURNAL_INVALID');}
}
function handle(root,loaded,recoveryOnly=false){
  let last=loaded.last,archived=false;
  const identity=Object.freeze({...loaded.header.identity});
  const active=loaded.active;
  return Object.freeze({identity,ownerPid:loaded.header.ownerPid,
    get current(){return Object.freeze({...last});},
    get events(){return load(root).events.map(x=>Object.freeze({...x}));},
    advance(stage,containerId=last.containerId){
      if(archived)fail('PILOT_JOURNAL_CLOSED');
      if(recoveryOnly&&!['CleanupPending','Cleaned'].includes(stage))fail('PILOT_RECOVERY_CLEANUP_ONLY');
      if(!recoveryOnly&&fs.existsSync(path.join(active,'recovery.lock')))fail('PILOT_RECOVERY_ALREADY_CLAIMED');
      const actual=load(root);
      if(actual.header.identity.attemptId!==identity.attemptId||
        JSON.stringify(actual.last)!==JSON.stringify(last))fail('PILOT_JOURNAL_CHANGED');
      const event={sequence:last.sequence+1,stage,containerId};
      checkEvent(last,event);writeDurable(path.join(active,'events.jsonl'),event,'a');last=event;
      return Object.freeze({...last});
    },
    archive(){
      if(archived||last.stage!=='Cleaned')fail('PILOT_CLEANUP_REQUIRED');
      const actual=load(root);
      if(actual.header.identity.attemptId!==identity.attemptId||actual.last.stage!=='Cleaned')
        fail('PILOT_JOURNAL_CHANGED');
      const destination=path.join(root,'attempt-'+identity.attemptId);
      if(fs.existsSync(destination))fail('PILOT_ATTEMPT_REPLAY');
      fs.renameSync(active,destination);archived=true;
      return identity.attemptId;
    }
  });
}
export function reservePilotAttempt(root,identity){
  if(!identityValid(identity))fail('PILOT_JOURNAL_IDENTITY_INVALID');
  root=rootPath(root);
  if(fs.existsSync(path.join(root,'attempt-'+identity.attemptId)))fail('PILOT_ATTEMPT_REPLAY');
  const active=path.join(root,'active');
  try{fs.mkdirSync(active,{mode:0o700});}catch{fail('PILOT_PRIOR_CLEANUP_REQUIRED');}
  // If a crash interrupts either write, leave active in place. No new
  // attempt or automatic removal is permitted from a partial identity.
  writeDurable(path.join(active,'identity.json'),{schemaVersion:1,ownerPid:process.pid,identity},'wx');
  writeDurable(path.join(active,'events.jsonl'),{sequence:0,stage:'Reserved',containerId:null},'wx');
  return handle(root,load(root));
}
export function readPilotAttempt(root){
  root=rootPath(root);
  if(!fs.existsSync(path.join(root,'active')))return null;
  const x=load(root);
  return Object.freeze({identity:Object.freeze({...x.header.identity}),ownerPid:x.header.ownerPid,
    current:Object.freeze({...x.last}),events:x.events.map(e=>Object.freeze({...e}))});
}
function processAlive(pid){
  try{process.kill(pid,0);return true;}catch(e){return e?.code!=='ESRCH';}
}
// Explicit recovery only. Never take over a live or PID-reused owner,
// never resume work. A second interrupted recovery stays blocked.
export function recoverPilotAttempt(root,{isAlive=processAlive}={}){
  root=rootPath(root);const x=load(root);
  if(isAlive(x.header.ownerPid))fail('PILOT_OWNER_STILL_ACTIVE');
  try{writeDurable(path.join(x.active,'recovery.lock'),{ownerPid:process.pid},'wx');}
  catch{fail('PILOT_RECOVERY_ALREADY_CLAIMED');}
  return handle(root,x,true);
}
