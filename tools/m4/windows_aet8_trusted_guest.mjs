// AET-8 owner-approved TRUSTED integrated qualification, never generated source.
// All payload entries are fixed reviewed sources, supplied by the pinned host.
import fs from 'node:fs';
import net from 'node:net';
import http from 'node:http';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
const PAYLOAD='__CA_AET8_FIXED_PAYLOAD__';
const sha=s=>createHash('sha256').update(s).digest('hex');
const decode=s=>Buffer.from(s,'base64').toString('utf8');
const load=s=>import('data:text/javascript;base64,'+s);
export const SECURITY_CASES=Object.freeze([
  'nonroot_seccomp_no_caps','no_host_mounts_or_socket','credentials_denied',
  'system_write_denied','symlink_write_denied','traversal_write_denied',
  'outbound_and_host_service_denied','child_network_denied','child_system_write_denied',
  'git_remote_denied',
]);
export const FIXTURE_CASES=Object.freeze([
  'same_guest_sandbox_compatibility','working_seven_checks','inert_negative_rejected',
  'forged_receipt_rejected','trusted_source_unchanged',
]);
export const FAILURE_STAGES=Object.freeze(['PAYLOAD','MODULES','SECURITY','COMPATIBILITY','SERVER','WORKING','BROKEN','SPOOF','COMPLETE']);
export const FAILURE_CODES=Object.freeze(['FIXED_SOURCE_MANIFEST_INVALID','FIXED_SOURCE_CHANGED','FULL_CHROMIUM_REQUIRED','BROWSER_PIPE_MISSING','BROWSER_PIPE_CLOSED','BROWSER_PIPE_BUSY','BROWSER_COMMAND_TIMEOUT','BROWSER_PROTOCOL_ERROR','BROWSER_STORAGE_RESET_FAILED','BROWSER_LAUNCH_FAILED','BROWSER_TARGET_FAILED','BROWSER_TARGET_ATTACH_FAILED','FIXTURE_DOM_EVALUATION_FAILED','FIXTURE_NAVIGATION_TIMEOUT','FIXTURE_NAVIGATION_FAILED','FIXTURE_CONTROL_MISSING','MISSING_RESULT','FIXTURE_CONTROL_NOT_LOADED','FIXED_FIXTURE_REQUIRED','BROWSER_RELOAD_TIMEOUT','SECURITY_OPERATION_FAILED','GUEST_STAGE_FAILED','BROWSER_EXIT_TIMEOUT','BROWSER_SHUTDOWN_NOT_GRACEFUL','GUEST_DEADLINE_EXCEEDED','BROWSER_GRACEFUL_EXIT_TIMEOUT','BROWSER_EXIT_SIGNAL','BROWSER_EXIT_NONZERO']);
const denied=f=>{try{f();return false;}catch{return true;}};
function connectDenied(host,port){
  return new Promise(resolve=>{
    const s=net.createConnection({host,port});let done=false;
    const finish=v=>{if(done)return;done=true;s.destroy();resolve(v);};
    s.setTimeout(1200,()=>finish(true));s.on('error',()=>finish(true));s.on('connect',()=>finish(false));
  });
}
function childDenied(code){
  const r=spawnSync(process.execPath,['-e',code],{encoding:'utf8',timeout:5000,maxBuffer:1024,env:{HOME:'/tmp',PATH:process.env.PATH}});
  return r.status===0&&r.stdout==='DENIED';
}
async function security(){
  const status=fs.readFileSync('/proc/self/status','utf8');
  const result={};
  result.nonroot_seccomp_no_caps=process.getuid?.()===1000&&/^Seccomp:\s*2\s*$/m.test(status)&&/^NoNewPrivs:\s*1\s*$/m.test(status)&&/^CapEff:\s*0+\s*$/m.test(status)&&/^CapBnd:\s*0+\s*$/m.test(status);
  result.no_host_mounts_or_socket=['/workspace','/host','/var/run/docker.sock','/run/docker.sock','/run/podman/podman.sock','/mnt/c/Users','/run/desktop/mnt/host/c/Users','/host_mnt/c/Users'].every(p=>!fs.existsSync(p));
  result.credentials_denied=denied(()=>fs.readFileSync('/etc/shadow'))&&denied(()=>fs.readFileSync('/proc/1/root/etc/shadow'))&&
    ['OPENAI_API_KEY','ANTHROPIC_API_KEY','GH_TOKEN','GITHUB_TOKEN','OLLAMA_HOST','CODINGASSISTANT_OWNER_TOKEN','AWS_SECRET_ACCESS_KEY','AZURE_OPENAI_API_KEY'].every(k=>!Object.hasOwn(process.env,k));
  result.system_write_denied=denied(()=>fs.writeFileSync('/etc/ca-aet8-write','BAD'));
  fs.symlinkSync('/etc/passwd','/tmp/ca-aet8-link');
  result.symlink_write_denied=denied(()=>fs.writeFileSync('/tmp/ca-aet8-link','BAD'));
  result.traversal_write_denied=denied(()=>fs.writeFileSync('/tmp/../etc/ca-aet8-traversal','BAD'));
  result.outbound_and_host_service_denied=await connectDenied('203.0.113.1',443)&&await connectDenied('127.0.0.1',11434);
  result.child_network_denied=childDenied("const s=require('node:net').connect({host:'203.0.113.1',port:443});let done=false;const f=x=>{if(done)return;done=true;s.destroy();process.stdout.write(x)};s.setTimeout(1000,()=>f('DENIED'));s.on('error',()=>f('DENIED'));s.on('connect',()=>f('BAD'))");
  result.child_system_write_denied=childDenied("try{require('node:fs').writeFileSync('/etc/ca-aet8-child','BAD');process.stdout.write('BAD')}catch{process.stdout.write('DENIED')}");
  const git=spawnSync('git',['-c','credential.helper=','ls-remote','https://203.0.113.1/ca-fixed-probe'],{encoding:'utf8',timeout:5000,maxBuffer:1024,env:{PATH:process.env.PATH,HOME:'/tmp',GIT_TERMINAL_PROMPT:'0'}});
  result.git_remote_denied=git.error?.code==='ENOENT'||(Number.isInteger(git.status)&&git.status!==0)||git.error?.code==='ETIMEDOUT';
  return result;
}
async function main(){
  let stage='PAYLOAD';
  let checks=null;
  const fixture=Object.fromEntries(FIXTURE_CASES.map(k=>[k,false]));
  const observations={working:null,broken:null,spoof:null};
  let failure=null;
  const snapshot=()=>({schemaVersion:1,security:checks,fixture,observations});
  const progress=()=>process.stdout.write(JSON.stringify({kind:'progress',stage,...snapshot()})+'\n');
  const move=next=>{stage=next;progress();};
  // Guest deadline is shorter than the unchanged host/container timeout.
  // This cannot authorize a pass; the host must still confirm teardown.
  const watchdog=setTimeout(()=>{
    const report={...snapshot(),failure:{stage,code:'GUEST_DEADLINE_EXCEEDED'}};
    process.stdout.write(JSON.stringify(report)+'\n',()=>process.exit(2));
  },145000);
  progress();
  try{
  const payload=JSON.parse(decode(PAYLOAD));
  const names=['working.html','working.js','broken.html','broken.js'];
  if(Object.keys(payload.sources).sort().join('|')!==[...names].sort().join('|'))throw Error('FIXED_SOURCE_MANIFEST_INVALID');
  const files=Object.fromEntries(names.map(n=>[n,decode(payload.sources[n])]));
  const before=Object.fromEntries(names.map(n=>[n,sha(files[n])]));
  if(names.some(n=>before[n]!==payload.hashes[n]))throw Error('FIXED_SOURCE_CHANGED');
  move('MODULES');
  const sandbox=await load(payload.sandbox),contract=await load(payload.contract),driver=await load(payload.driver);
  move('SECURITY');
  checks=await security();progress();
  if(SECURITY_CASES.every(k=>checks[k]===true)){
    move('COMPATIBILITY');
    const compatibility=await sandbox.runFixedCompatibility();
    fixture.same_guest_sandbox_compatibility=sandbox.CASES.every(k=>compatibility.cases[k]===true);progress();
    if(fixture.same_guest_sandbox_compatibility){
      // Frozen forged-receipt negative control, not a model-supplied script.
      const spoofHtml=files['working.html'].replace('./working.js','./spoof.js');
      const spoofJs='document.body.dataset.verified="true";window.verification={status:"VERIFIED",passed:7};';
      const routes={...files,'spoof.html':spoofHtml,'spoof.js':spoofJs};
      move('SERVER');
      const server=http.createServer((req,res)=>{
        const key=req.url?.slice(1);
        if(req.method!=='GET'||!Object.hasOwn(routes,key)){res.writeHead(404);res.end();return;}
        res.writeHead(200,{'Content-Type':key.endsWith('.js')?'application/javascript; charset=utf-8':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
        res.end(routes[key]);
      });
      await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
      try{
        for(const name of ['working','broken','spoof']){
          move(name.toUpperCase());
          const d=await driver.createTrustedDriver('http://127.0.0.1:'+server.address().port,name);
          try{
            // First verify the negative control really loads with the expected
            // inert display. A failed browser launch is not a negative pass.
            const s=await d.openFresh();
            let loaded;
            try{
              loaded=(await s.page.locator('output#display').innerText()).trim()==='0';
              if(name!=='working'){
                await s.page.keyboard.type('2+3');await s.page.keyboard.press('Enter');
                loaded=loaded&&(await s.page.locator('output#display').innerText()).trim()==='0';
              }
            }finally{await s.close();}
            if(!loaded)throw Error('FIXTURE_CONTROL_NOT_LOADED');
            const report=await contract.evaluateCalculationJournal(d.openFresh,{onCaseError:error=>{
              // Missing controls are genuine negative observations. A broken
              // driver is not evidence that an inert/forged app was rejected.
              if(!['FIXTURE_CONTROL_MISSING','MISSING_RESULT'].includes(error?.message))throw error;
            }});
            observations[name]=report.checks;
            const key={working:'working_seven_checks',broken:'inert_negative_rejected',spoof:'forged_receipt_rejected'}[name];
            fixture[key]=contract.CALCULATION_CASES.every(k=>report.checks[k]===(name==='working'));progress();
          }finally{await d.close();}
        }
      }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
      fixture.working_seven_checks=contract.CALCULATION_CASES.every(k=>observations.working?.[k]===true);
      fixture.inert_negative_rejected=contract.CALCULATION_CASES.every(k=>observations.broken?.[k]===false);
      fixture.forged_receipt_rejected=contract.CALCULATION_CASES.every(k=>observations.spoof?.[k]===false);
    }
  }
  fixture.trusted_source_unchanged=names.every(n=>before[n]===sha(files[n]));
  move('COMPLETE');
  }catch(e){
    const code=FAILURE_CODES.includes(e?.message)?e.message:stage==='SECURITY'?'SECURITY_OPERATION_FAILED':'GUEST_STAGE_FAILED';
    failure={stage,code};
  }
  clearTimeout(watchdog);
  const report={...snapshot(),...(failure?{failure}:{})};
  const code=failure||!SECURITY_CASES.every(k=>checks?.[k]===true)||!FIXTURE_CASES.every(k=>fixture[k]===true)?2:0;
  // Flush the final receipt, then exit even if a Chromium descendant kept
  // a descriptor alive. Host removal is an independent mandatory gate.
  process.stdout.write(JSON.stringify(report)+'\n',()=>process.exit(code));
}
if(process.argv.includes('--run-trusted-aet8')){
  try{await main();}catch{process.stdout.write(JSON.stringify({blocked:true})+'\n');process.exitCode=2;}
}
