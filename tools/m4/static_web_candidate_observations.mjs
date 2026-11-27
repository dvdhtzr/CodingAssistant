// Independent candidate result envelope for future isolated driver.
 // Parsing only: no launch, grant, trust establishment or verified state.
import {CALCULATION_CASES} from './calculation_journal_contract.mjs';
import {SECURITY_CASES} from './windows_aet8_trusted_guest.mjs';
const plain=x=>x!==null&&typeof x==='object'&&!Array.isArray(x);
const exact=(x,keys)=>plain(x)&&Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const fail=()=>{throw Error('CANDIDATE_OBSERVATIONS_INVALID');};
const BINDINGS=['attemptId','candidateSha256','archiveSha256','evaluatorSha256','adapterSha256','policySha256'];
const BUILD=['exact_source_hashes','javascript_syntax','local_assets_linked','source_integrity_unchanged'];
const INFRA=['browser_sandbox_confirmed','independent_driver_confirmed','browser_closed'];
const reportKeys=['schemaVersion',...BINDINGS,'security','build','infrastructure','cases'];
const booleans=(x,keys,nullable=false)=>exact(x,keys)&&keys.every(k=>typeof x[k]==='boolean'||(nullable&&x[k]===null));
const all=(x,keys)=>keys.every(k=>x[k]===true);
export function parseCandidateObservations(raw,expected){
  if(typeof raw!=='string'||raw.length<2||raw.length>12000||
    !exact(expected,BINDINGS)||typeof expected.attemptId!=='string'||
    expected.attemptId.length!==32||!/^[a-f0-9]{32}$/.test(expected.attemptId)||
    !BINDINGS.slice(1).every(k=>digest(expected[k])))fail();
  let result;try{result=JSON.parse(raw);}catch{fail();}
  if(!exact(result,reportKeys)||result.schemaVersion!==1||!BINDINGS.every(k=>result[k]===expected[k])||
    !booleans(result.security,SECURITY_CASES)||!booleans(result.build,BUILD,true)||
    !booleans(result.infrastructure,INFRA,true)||!booleans(result.cases,CALCULATION_CASES,true))fail();
  const securityOk=all(result.security,SECURITY_CASES);
  const buildOk=all(result.build,BUILD);
  const infraOk=all(result.infrastructure,INFRA);
  const completed=CALCULATION_CASES.filter(k=>typeof result.cases[k]==='boolean').length;
  // An unsafe guest cannot produce usable build/case evidence. Failed
  // build may not report functional tests. Infrastructure failures may
  // retain partial observations but never become a functional failure.
  if(!securityOk&&[...Object.values(result.build),...Object.values(result.infrastructure),...Object.values(result.cases)].some(v=>v!==null))fail();
  if(!buildOk&&completed!==0)fail();
  const passed=CALCULATION_CASES.filter(k=>result.cases[k]===true).length;
  const status=!securityOk?'VERIFICATION_BLOCKED':
    BUILD.some(k=>result.build[k]===false)?'BUILD_FAILED':
    !buildOk||!infraOk||completed!==CALCULATION_CASES.length?'VERIFICATION_BLOCKED':
    passed===CALCULATION_CASES.length?'CHECKS_PASSED_NOT_VERIFIED':'TESTS_FAILED';
  // This is guest observation classification only. Current host proof,
  // authenticated transport, process exit and cleanup are still required.
  return Object.freeze({status,passed,completed,required:CALCULATION_CASES.length,
    cases:Object.freeze({...result.cases}),security:Object.freeze({...result.security}),
    build:Object.freeze({...result.build}),infrastructure:Object.freeze({...result.infrastructure}),
    executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
}
