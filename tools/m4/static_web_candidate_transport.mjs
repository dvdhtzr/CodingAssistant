// Non-executable transport assembly for the narrow Calculation Journal pilot.
// Reuse the *identical* qualified guest template, replacing only its data
// marker. No host launch, guest evaluation, shell command or file extraction.
// A complete historical binding is necessary but not an executable grant.
import {createHash} from 'node:crypto';
import {prepareEvaluationAdmission} from './static_web_evaluation_admission.mjs';
import {prepareCandidatePayload} from './static_web_candidate_payload.mjs';
import {fixedCandidateBundle} from './windows_candidate_fixture_qualification.mjs';
import {bindCompletePilotEvidence,bindBuildQualifiedPilotEvidence,bindPinnedDispatchEvidence} from './static_web_pilot_evidence.mjs';
import {buildQualifiedCandidateBundle} from './static_web_build_qualification.mjs';
import {preparePilotPolicy} from './static_web_pilot_policy.mjs';
import {createCandidateAssets} from './static_web_candidate_assets.mjs';

const sha=x=>createHash('sha256').update(x).digest('hex');
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const digest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const fail=code=>{throw Error(code);};
const MARKER='__CA_FIXED_CANDIDATE_INPUT__';
export function prepareReviewedCandidateTransport(inputs,pilotInputs,selection,dispatchPins){
  if(!exact(selection,['attemptId','admissionSha256','candidateSha256','archiveSha256',
    'completeEvidenceSha256','policySha256','adapterSha256'])||
    typeof selection.attemptId!=='string'||!/^[a-f0-9]{32}$/.test(selection.attemptId)||selection.attemptId.length!==32||
    !Object.keys(selection).filter(k=>k!=='attemptId').every(k=>digest(selection[k])))
    fail('CANDIDATE_TRANSPORT_SELECTION_INVALID');
  // Both sides are reconstructed from actual inputs, never a caller-supplied
  // pass descriptor. No permission is inferred from the selection hashes.
  const upgraded=Object.hasOwn(pilotInputs,'build')&&pilotInputs.build!==undefined;
  const pinned=pilotInputs.dispatch!==undefined||pilotInputs.pinnedInterruptions!==undefined;
  const historical=pinned?bindPinnedDispatchEvidence(pilotInputs,dispatchPins??{}):
    upgraded?bindBuildQualifiedPilotEvidence(pilotInputs):bindCompletePilotEvidence(pilotInputs);
  const evidenceHash=pinned?historical.dispatchEvidenceSha256:upgraded?historical.qualifiedEvidenceSha256:historical.completeEvidenceSha256;
  const selectedPolicy=upgraded?historical.buildQualification:historical;
  const admission=prepareEvaluationAdmission(inputs);
  const bundle=upgraded?buildQualifiedCandidateBundle():fixedCandidateBundle();
  const policy=preparePilotPolicy(pilotInputs.imageMetadata,bundle.adapterSha256,bundle.evaluatorSha256);
  const fixture=admission.qualification;
  if(evidenceHash!==selection.completeEvidenceSha256||
    selectedPolicy.policySha256!==policy.policySha256||policy.policySha256!==selection.policySha256||
    bundle.adapterSha256!==selection.adapterSha256||
    admission.admissionSha256!==selection.admissionSha256||
    admission.candidateSha256!==selection.candidateSha256||admission.archiveSha256!==selection.archiveSha256||
    admission.evaluatorSha256!==bundle.evaluatorSha256||
    fixture.imageId!==policy.descriptor.imageId||fixture.registryDigest!==policy.descriptor.registryDigest||
    fixture.seccompSha256!==policy.descriptor.seccompSha256||fixture.fixturePidLimit!==policy.descriptor.pidLimit)
    fail('CANDIDATE_TRANSPORT_BINDING_CHANGED');
  if([...Object.values(historical.attempts).map(x=>x.attemptId),historical.boundary.attemptId,
    ...Object.values(historical.buildQualification?.attempts??{}).map(x=>x.attemptId),
    ...Object.values(historical.dispatchQualification?.attempts??{}).map(x=>x.attemptId)].includes(selection.attemptId))
    fail('CANDIDATE_TRANSPORT_ATTEMPT_REUSED');
  const staged=prepareCandidatePayload(inputs,selection.admissionSha256);
  createCandidateAssets(staged.payloadJson,staged.binding);
  const expected=Object.freeze({attemptId:selection.attemptId,candidateSha256:staged.binding.candidateSha256,
    archiveSha256:staged.binding.archiveSha256,evaluatorSha256:bundle.evaluatorSha256,
    adapterSha256:bundle.adapterSha256,policySha256:policy.policySha256});
  const request=JSON.stringify({payloadJson:staged.payloadJson,binding:staged.binding,expected});
  if(Buffer.byteLength(request)>2*1024*1024||bundle.source.split(MARKER).length!==2||
    sha(bundle.source)!==bundle.adapterSha256)fail('CANDIDATE_TRANSPORT_TEMPLATE_INVALID');
  const encoded=Buffer.from(request,'utf8').toString('base64');
  const source=bundle.source.replace(MARKER,()=>encoded);
  // Check the reverse substitution exactly; candidate bytes cannot become
  // guest code through quotes, backticks, newlines or marker text.
  if(source.replace(encoded,()=>MARKER)!==bundle.source||Buffer.byteLength(source)>4*1024*1024)
    fail('CANDIDATE_TRANSPORT_TEMPLATE_INVALID');
  const descriptor=Object.freeze({schemaVersion:1,status:'CANDIDATE_TRANSPORT_PREPARED_ONLY',
    ...expected,admissionSha256:admission.admissionSha256,
    completeEvidenceSha256:evidenceHash,engineId:historical.engineId,
    sourceFingerprint:staged.binding.sourceFingerprint,assetCount:staged.binding.files.length,
    payloadSha256:sha(request),transportSha256:sha(source),transportBytes:Buffer.byteLength(source),
    executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
  // No caller can dispatch this. The older fixture-only preparation is
  // retained for diagnostics; only upgraded evidence selects the new build gate.
  return Object.freeze({descriptor,source,expected});
}
