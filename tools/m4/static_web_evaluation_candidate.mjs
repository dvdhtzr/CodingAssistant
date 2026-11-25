// AET-8 host-owned, read-only candidate binding. NOT executable admission.
// Only the authenticated host may supply currentScope from its task store.
// An exact UI selection is not owner execution consent. No Docker, browser,
// network, extraction or process operations are performed here.
import {createHash} from 'node:crypto';
import fs from 'node:fs';
import {parseTrustedStaticZip} from './smoke_generated_zip.mjs';
import {CALCULATION_CONTRACT_VERSION,CALCULATION_CASES} from './calculation_journal_contract.mjs';

const sha=value=>createHash('sha256').update(value).digest('hex');
const digest=value=>typeof value==='string'&&/^[a-f0-9]{64}$/.test(value);
const id=value=>typeof value==='string'&&/^[a-f0-9]{32}$/.test(value);
const ordinal=(a,b)=>a<b?-1:a>b?1:0;
const keys=['taskId','engineeringRunId','planRevision','planSha256','specificationVersion',
  'specificationSha256','architectureSha256','model','sourceFingerprint','scopeSha256'];
const contractSha=sha(fs.readFileSync(new URL('./calculation_journal_contract.mjs',import.meta.url),'utf8').replace(/\r\n/g,'\n'));
const fail=code=>{throw Error(code);};
function validScope(s){
  return s&&s.schemaVersion===1&&s.status==='SCOPE_CANDIDATE'&&
    s.executionEnabled===false&&s.verificationStatus==='GENERATED_NOT_TESTED'&&
    Array.isArray(s.blockers)&&s.blockers.length===0&&
    id(s.taskId)&&id(s.engineeringRunId)&&
    Number.isSafeInteger(s.planRevision)&&s.planRevision>0&&
    Number.isSafeInteger(s.specificationVersion)&&s.specificationVersion>0&&
    ['planSha256','specificationSha256','architectureSha256','sourceFingerprint','scopeSha256'].every(k=>digest(s[k]))&&
    typeof s.model==='string'&&s.model.length>0&&s.model.length<=200;
}
export function prepareEvaluationCandidate({currentScope,selection,archive}={}){
  if(!validScope(currentScope)||!selection||!keys.every(k=>selection[k]===currentScope[k]))
    fail('EVALUATION_SCOPE_NOT_CURRENT');
  const source=parseTrustedStaticZip(archive);
  const manifest=source.map(f=>({path:f.path,sha256:f.sha256,utf8Bytes:Buffer.byteLength(f.content,'utf8')}));
  manifest.sort((a,b)=>ordinal(a.path,b.path));
  const declared=currentScope.files;
  if(!Array.isArray(declared)||declared.length!==manifest.length||
    !manifest.every((f,i)=>declared[i]?.path===f.path&&declared[i]?.sha256===f.sha256&&declared[i]?.utf8Bytes===f.utf8Bytes))
    fail('EVALUATION_SOURCE_MANIFEST_MISMATCH');
  const actual=sha([...manifest].sort((a,b)=>ordinal(a.path.toUpperCase(),b.path.toUpperCase()))
    .map(f=>f.path+'\t'+f.sha256).join('\n'));
  if(actual!==currentScope.sourceFingerprint)fail('EVALUATION_SOURCE_FINGERPRINT_MISMATCH');
  const binding=Object.fromEntries(keys.map(k=>[k,currentScope[k]]));
  const descriptor={schemaVersion:1,...binding,archiveSha256:sha(archive),
    evaluatorVersion:CALCULATION_CONTRACT_VERSION,evaluatorSha256:contractSha,
    checks:[...CALCULATION_CASES],files:manifest};
  return Object.freeze({...descriptor,descriptorSha256:sha(JSON.stringify(descriptor)),
    status:'CANDIDATE_ONLY',executionEnabled:false,generatedArtifactVerified:false,
    runtimeQualified:false,checks:Object.freeze(descriptor.checks),
    files:Object.freeze(manifest.map(f=>Object.freeze(f)))});
}
