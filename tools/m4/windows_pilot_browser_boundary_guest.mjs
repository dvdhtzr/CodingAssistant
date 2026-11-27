// Frozen renderer boundary qualification only. The host substitutes the
// exact reviewed data-URL module closure and fixed fixture manifest.
import {createCandidateAssets} from '__ASSETS__';
import {openCandidateAssetServer} from '__SERVER__';
import {createCandidateDriver} from '__DRIVER__';
import {security,SECURITY_CASES} from '__SECURITY__';
import {runFixedCompatibility,CASES} from '__SANDBOX__';
const INPUT='__CA_FIXED_CANDIDATE_INPUT__';
const input=JSON.parse(Buffer.from(INPUT,'base64').toString('utf8'));
const deadline=setTimeout(()=>process.exit(2),145000);
let server,driver,session;
const checks={guest_security:false,chromium_sandbox:false,renderer_controls:false,browser_closed:false,server_closed:false};
try{
  if(process.platform!=='linux'||process.getuid?.()!==1000||!process.argv.includes('--run-fixed-candidate-fixture'))
    throw Error('BOUNDARY_GUEST_REQUIRED');
  const securityChecks=await security();
  checks.guest_security=SECURITY_CASES.every(k=>securityChecks[k]===true);
  if(!checks.guest_security)throw Error('BOUNDARY_SECURITY_FAILED');
  const compatibility=await runFixedCompatibility();
  checks.chromium_sandbox=CASES.every(k=>compatibility.cases[k]===true);
  if(!checks.chromium_sandbox)throw Error('BOUNDARY_SANDBOX_FAILED');
  const assets=createCandidateAssets(input.payloadJson,input.binding);
  server=await openCandidateAssetServer(assets);
  driver=createCandidateDriver(server.origin);session=await driver.openFresh();
  // The probe is trusted checked-in code, not a candidate's self-report.
  // Read its observable result using the existing isolated-world driver.
  const until=Date.now()+20000;
  while(Date.now()<until){
    const value=(await session.page.locator('output#display').innerText()).trim();
    if(value==='BOUNDARY_CONTROLS_PASS'){checks.renderer_controls=true;break;}
    if(value==='BOUNDARY_CONTROLS_FAIL')break;
    await new Promise(resolve=>setTimeout(resolve,100));
  }
}catch{}finally{
  if(session)try{await session.close();}catch{}
  if(driver)try{await driver.close();checks.browser_closed=true;}catch{}
  if(server)try{await server.close();checks.server_closed=true;}catch{}
  clearTimeout(deadline);
}
const ok=Object.values(checks).every(x=>x===true);
process.stdout.write(JSON.stringify({schemaVersion:1,...input.expected,checks})+'\n',()=>process.exit(ok?0:2));
