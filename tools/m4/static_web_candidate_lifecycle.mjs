// Guest evaluation composition. No CLI, owner authorization, Docker launch
// or runtime promotion. The separately qualified host must supply trusted
// security/build/sandbox operations; no candidate-supplied hooks are accepted.
import {createCandidateAssets} from './static_web_candidate_assets.mjs';
import {openCandidateAssetServer} from './static_web_candidate_server.mjs';
import {createCandidateDriver} from './static_web_candidate_driver.mjs';
import {CALCULATION_CASES,evaluateCalculationJournal} from './calculation_journal_contract.mjs';
import {SECURITY_CASES} from './windows_aet8_trusted_guest.mjs';
import {parseCandidateObservations} from './static_web_candidate_observations.mjs';

const BUILD=['exact_source_hashes','javascript_syntax','local_assets_linked','source_integrity_unchanged'];
const INFRA=['browser_sandbox_confirmed','independent_driver_confirmed','browser_closed','asset_server_closed'];
const empty=keys=>Object.fromEntries(keys.map(k=>[k,null]));
const exactBooleans=(value,keys)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
  Object.keys(value).length===keys.length&&keys.every(k=>Object.hasOwn(value,k)&&typeof value[k]==='boolean');
const functionalErrors=new Set(['MISSING_RESULT','FIXTURE_CONTROL_MISSING']);
export async function observeCandidate({payloadJson,binding,expected},{securityProbe,buildProbe,sandboxProbe,
  openServer=openCandidateAssetServer,createDriver=createCandidateDriver}={}){
  const report={schemaVersion:1,...expected,security:empty(SECURITY_CASES),build:empty(BUILD),
    infrastructure:empty(INFRA),cases:empty(CALCULATION_CASES)};
  // Validate the attempt identity even before requesting trusted operations.
  parseCandidateObservations(JSON.stringify(report),expected);
  if(binding?.candidateSha256!==expected.candidateSha256||
    binding?.archiveSha256!==expected.archiveSha256)
    throw Error('CANDIDATE_ATTEMPT_BINDING_CHANGED');
  if(![securityProbe,buildProbe,sandboxProbe,openServer,createDriver].every(x=>typeof x==='function'))
    throw Error('CANDIDATE_TRUSTED_OPERATIONS_REQUIRED');
  // Freeze our expected manifest before any asynchronous work. File bytes
  // remain strings and never enter an execution or filesystem-write API.
  const manifest=Object.freeze({...binding,files:Object.freeze(binding.files.map(f=>Object.freeze({...f})))});
  let server,driver,assets,securityOk=false;
  try{
    const checks=await securityProbe();
    if(!exactBooleans(checks,SECURITY_CASES))throw Error('CANDIDATE_SECURITY_OBSERVATIONS_INVALID');
    report.security={...checks};
    securityOk=SECURITY_CASES.every(k=>checks[k]===true);
    if(securityOk){
      try{
        assets=createCandidateAssets(payloadJson,manifest);
        report.build.exact_source_hashes=true;
      }catch{
        report.build.exact_source_hashes=false;
      }
      if(assets){
        const build=await buildProbe(assets,manifest.files);
        if(!exactBooleans(build,['javascript_syntax','local_assets_linked']))
          throw Error('CANDIDATE_BUILD_OBSERVATIONS_INVALID');
        Object.assign(report.build,build);
        if(build.javascript_syntax&&build.local_assets_linked){
          report.infrastructure.browser_sandbox_confirmed=await sandboxProbe()===true;
          if(report.infrastructure.browser_sandbox_confirmed){
            server=await openServer(assets);
            driver=await createDriver(server.origin);
            if(typeof driver?.openFresh!=='function'||typeof driver?.close!=='function')
              throw Error('CANDIDATE_DRIVER_UNAVAILABLE');
            report.infrastructure.independent_driver_confirmed=true;
            await evaluateCalculationJournal(()=>driver.openFresh(),{
              onCaseError(error){
                // A missing required UI element is a functional observation.
                // Transport/session/navigation failures interrupt the sequence.
                if(!functionalErrors.has(error?.message))throw error;
              },
              onCaseComplete(name,passed){report.cases[name]=passed;}
            });
          }
        }
      }
    }
  }catch{
    // Do not retain arbitrary child error text. Partial observations remain
    // non-authoritative; failures in a trusted operation block verification.
    if(securityOk)report.infrastructure.independent_driver_confirmed=false;
  }finally{
    if(driver){
      try{await driver.close();report.infrastructure.browser_closed=true;}
      catch{report.infrastructure.browser_closed=false;}
    }
    if(server){
      try{await server.close();report.infrastructure.asset_server_closed=true;}
      catch{report.infrastructure.asset_server_closed=false;}
    }
    if(assets){
      try{
        const again=createCandidateAssets(payloadJson,manifest);
        report.build.source_integrity_unchanged=again.sourceFingerprint===assets.sourceFingerprint;
      }catch{report.build.source_integrity_unchanged=false;}
    }
  }
  // Re-parse exactly what a future authenticated guest transport would
  // return. This does not authenticate the transport or establish cleanup.
  const raw=JSON.stringify(report);
  return Object.freeze({raw,observations:parseCandidateObservations(raw,expected)});
}
