// Non-executing host contract for the next independent verification lane.
// All inputs must originate from the authenticated control plane, never a
// generated page, model or caller-supplied "verified" claim. A validated
// descriptor or planning acknowledgement is NOT a runnable authorization.
// No process launch, Docker invocation, file extraction or database write.
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {prepareEvaluationCandidate} from './static_web_evaluation_candidate.mjs';
import {CALCULATION_CASES,CALCULATION_CONTRACT_VERSION} from './calculation_journal_contract.mjs';
import {parseTrustedReport,trustedPayload} from './windows_aet8_trusted_qualification.mjs';
import {IMAGE_TAG,validatePinnedImage} from './windows_docker_qualification.mjs';
import {checkedChromiumSeccomp,FIXTURE_PID_LIMIT} from './windows_chromium_sandbox_qualification.mjs';

const sha=x=>createHash('sha256').update(x).digest('hex');
const read=name=>fs.readFileSync(new URL(name,import.meta.url),'utf8').replace(/\r\n/g,'\n');
const digest=x=>typeof x==='string'&&/^[a-f0-9]{64}$/.test(x);
const plain=x=>x!==null&&typeof x==='object'&&!Array.isArray(x);
const exact=(x,keys)=>plain(x)&&Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const fail=code=>{throw Error(code);};
const ordinal=(a,b)=>a<b?-1:a>b?1:0;
const receiptFields=['schemaVersion','stage','status','recordedAtUtc','imageTag','imageId','registryDigest',
  'seccompSha256','fixturePidLimit','evaluatorVersion','evaluatorSha256','driverSha256','guestSha256',
  'trustedSourceHashes','security','fixture','observations','containerRemoved','generatedSourceExecuted',
  'generatedCodeExecutionEnabled','runtimeQualified','generatedArtifactVerified','reason','guestFailure','lastGuestStage'];

// This validates a trusted fixture receipt's shape and current code/image
// binding. It does not attest the machine on which a supplied report ran.
// A future real adapter must obtain the report directly from a trusted
// local qualification run, establish host provenance, and repeat security
// checks in the actual disposable execution profile.
export function bindTrustedFixtureReceipt(report,currentImageMetadata){
  if(!exact(report,receiptFields)||report.schemaVersion!==1||
    report.stage!=='AET-8-trusted-integrated-calculation'||
    report.status!=='TRUSTED_INTEGRATED_EVALUATOR_PASS'||
    typeof report.recordedAtUtc!=='string'||!Number.isFinite(Date.parse(report.recordedAtUtc))||
    report.containerRemoved!==true||report.reason!==null||report.guestFailure!==null||
    !['COMPLETE',null].includes(report.lastGuestStage)||
    ['generatedSourceExecuted','generatedCodeExecutionEnabled','runtimeQualified','generatedArtifactVerified']
      .some(k=>report[k]!==false))fail('TRUSTED_FIXTURE_RECEIPT_INVALID');
  const validated=parseTrustedReport(JSON.stringify({schemaVersion:1,security:report.security,
    fixture:report.fixture,observations:report.observations}));
  if(!Object.values(validated.security).every(v=>v===true)||
    !Object.values(validated.fixture).every(v=>v===true))
    fail('TRUSTED_FIXTURE_CHECKS_INCOMPLETE');
  const image=validatePinnedImage(currentImageMetadata);
  const payload=trustedPayload();
  if(report.imageTag!==IMAGE_TAG||report.imageId!==image.imageId||
    report.registryDigest!==image.registryDigest||
    report.seccompSha256!==checkedChromiumSeccomp().sha256||
    report.fixturePidLimit!==FIXTURE_PID_LIMIT||
    report.evaluatorVersion!==CALCULATION_CONTRACT_VERSION||
    report.evaluatorSha256!==sha(read('./calculation_journal_contract.mjs'))||
    report.driverSha256!==sha(read('./windows_aet8_pipe_driver.mjs'))||
    report.guestSha256!==sha(payload.source)||
    !exact(report.trustedSourceHashes,Object.keys(payload.hashes))||
    !Object.entries(payload.hashes).every(([key,value])=>report.trustedSourceHashes[key]===value))
    fail('TRUSTED_FIXTURE_BINDING_CHANGED');
  // Normalize before hashing: field ordering in receipt JSON is immaterial.
  const canonical=value=>Array.isArray(value)?value.map(canonical):plain(value)?
    Object.fromEntries(Object.keys(value).sort(ordinal).map(k=>[k,canonical(value[k])])):value;
  return Object.freeze({receiptSha256:sha(JSON.stringify(canonical(report))),
    recordedAtUtc:report.recordedAtUtc,imageId:image.imageId,registryDigest:image.registryDigest,
    seccompSha256:report.seccompSha256,fixturePidLimit:report.fixturePidLimit,
    evaluatorSha256:report.evaluatorSha256,driverSha256:report.driverSha256,
    guestSha256:report.guestSha256,hostProvenanceEstablished:false,
    runtimeQualified:false,executionEnabled:false});
}

function cleanText(value,max){
  return typeof value==='string'&&value.length>0&&value.length<=max&&
    value.trim()===value&&!/[\u0000-\u001f\u007f]/.test(value);
}

// The first reviewed contract has fixed cases. The host must read the full
// exact acceptance criteria from its current approved plan, and the owner
// must review the mapping. Coverage is necessary, not proof that a mapping
// is semantically correct. A calculator contract is never automatically
// selected just because the project is Static Web.
function bindCoverage(scope,criteria,reviewedContract){
  if(!Array.isArray(criteria)||criteria.length<1||criteria.length>64||
    criteria.some(c=>!exact(c,['id','text'])||!cleanText(c.id,200)||!cleanText(c.text,2000))||
    new Set(criteria.map(c=>c.id)).size!==criteria.length)
    fail('EVALUATION_CRITERIA_INVALID');
  if(!exact(reviewedContract,['contractId','planSha256','specificationSha256','architectureSha256','mappings'])||
    reviewedContract.contractId!==CALCULATION_CONTRACT_VERSION||
    reviewedContract.planSha256!==scope.planSha256||
    reviewedContract.specificationSha256!==scope.specificationSha256||
    reviewedContract.architectureSha256!==scope.architectureSha256||
    !Array.isArray(reviewedContract.mappings)||reviewedContract.mappings.length!==criteria.length)
    fail('EVALUATION_CONTRACT_NOT_CURRENT');
  const ids=new Set(),cases=new Set(),mappings=[];
  for(const m of reviewedContract.mappings){
    if(!exact(m,['criterionId','criterionText','cases'])||
      !criteria.some(c=>c.id===m.criterionId&&c.text===m.criterionText)||
      ids.has(m.criterionId)||!Array.isArray(m.cases)||m.cases.length<1||
      m.cases.length>CALCULATION_CASES.length||new Set(m.cases).size!==m.cases.length||
      m.cases.some(c=>!CALCULATION_CASES.includes(c)))
      fail('EVALUATION_CONTRACT_COVERAGE_INVALID');
    ids.add(m.criterionId);
    for(const c of m.cases)cases.add(c);
    mappings.push(Object.freeze({criterionId:m.criterionId,criterionText:m.criterionText,
      cases:Object.freeze([...m.cases].sort(ordinal))}));
  }
  if(CALCULATION_CASES.some(c=>!cases.has(c)))fail('EVALUATION_MANDATORY_CASE_MISSING');
  return Object.freeze(mappings.sort((a,b)=>ordinal(a.criterionId,b.criterionId)));
}

export function prepareEvaluationAdmission({currentScope,selection,archive,criteria,reviewedContract,
  qualificationReport,currentImageMetadata}={}){
  const candidate=prepareEvaluationCandidate({currentScope,selection,archive});
  const mappings=bindCoverage(currentScope,criteria,reviewedContract);
  const fixture=bindTrustedFixtureReceipt(qualificationReport,currentImageMetadata);
  if(candidate.evaluatorSha256!==fixture.evaluatorSha256)fail('EVALUATOR_IDENTITY_CHANGED');
  const descriptor={schemaVersion:1,candidateSha256:candidate.descriptorSha256,
    taskId:candidate.taskId,runId:candidate.engineeringRunId,planSha256:candidate.planSha256,
    specificationSha256:candidate.specificationSha256,architectureSha256:candidate.architectureSha256,
    sourceFingerprint:candidate.sourceFingerprint,scopeSha256:candidate.scopeSha256,
    archiveSha256:candidate.archiveSha256,model:candidate.model,contractId:candidate.evaluatorVersion,
    evaluatorSha256:candidate.evaluatorSha256,qualification:fixture,mappings};
  return Object.freeze({...descriptor,admissionSha256:sha(JSON.stringify(descriptor)),
    status:'ADMISSION_PREVIEW_ONLY',executionEnabled:false,runtimeQualified:false,
    generatedArtifactVerified:false,blockers:Object.freeze([
      'HOST_PROVENANCE_NOT_ESTABLISHED','EXECUTION_ADAPTER_NOT_QUALIFIED',
      'DURABLE_OWNER_EXECUTION_AUTHORIZATION_REQUIRED'])});
}

// Explicit planning review only. The trusted caller must recompute the
// current descriptor from the current database, archive and host inputs;
// the request can provide only the exact acknowledgement. This does not
// consume a grant, permit replay, launch anything, or mark a test passed.
export function acknowledgeEvaluationPlan(currentInputs,request){
  const preview=prepareEvaluationAdmission(currentInputs);
  if(!exact(request,['admissionSha256','confirmation'])||
    !digest(request.admissionSha256)||request.admissionSha256!==preview.admissionSha256||
    request.confirmation!=='REVIEW EVALUATION '+preview.admissionSha256)
    fail('EVALUATION_PLAN_CONFIRMATION_REQUIRED');
  return Object.freeze({admissionSha256:preview.admissionSha256,
    status:'PLAN_REVIEWED_NON_EXECUTABLE',executionEnabled:false,
    runtimeQualified:false,generatedArtifactVerified:false,blockers:preview.blockers});
}
