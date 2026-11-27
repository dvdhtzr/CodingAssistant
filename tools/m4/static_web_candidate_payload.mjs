// Non-executable host staging for the candidate-shaped evaluation lane.
// Recompute the entire admission from current trusted host inputs; never
// treat a user/model-supplied preview or raw archive as an authorization.
import {prepareEvaluationAdmission} from './static_web_evaluation_admission.mjs';
import {prepareEvaluationCandidate} from './static_web_evaluation_candidate.mjs';
import {parseTrustedStaticZip} from './smoke_generated_zip.mjs';
import {createCandidateAssets} from './static_web_candidate_assets.mjs';
export function prepareCandidatePayload(inputs,expectedAdmissionSha256){
  const admission=prepareEvaluationAdmission(inputs);
  if(admission.admissionSha256!==expectedAdmissionSha256)
    throw Error('CANDIDATE_ADMISSION_CHANGED');
  const candidate=prepareEvaluationCandidate(inputs);
  if(candidate.descriptorSha256!==admission.candidateSha256)
    throw Error('CANDIDATE_ADMISSION_CHANGED');
  const files=parseTrustedStaticZip(inputs.archive);
  const binding=Object.freeze({admissionSha256:admission.admissionSha256,
    candidateSha256:candidate.descriptorSha256,archiveSha256:candidate.archiveSha256,
    sourceFingerprint:candidate.sourceFingerprint,files:candidate.files});
  const payloadJson=JSON.stringify({schemaVersion:1,
    admissionSha256:binding.admissionSha256,candidateSha256:binding.candidateSha256,
    archiveSha256:binding.archiveSha256,sourceFingerprint:binding.sourceFingerprint,
    files:binding.files.map(f=>({...f,content:files.find(x=>x.path===f.path).content}))});
  // Validate guest-shaped input now, but do not start anything.
  createCandidateAssets(payloadJson,binding);
  return Object.freeze({binding,payloadJson,status:'CANDIDATE_PAYLOAD_ONLY',
    executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
}
