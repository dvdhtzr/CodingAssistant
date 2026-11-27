// Read-only candidate transport preview. Returns hashes and lengths only;
// never returns the guest program, raw archive or candidate text. No Docker
// commands other than fixed local metadata queries are used.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {readLocalReceipt,readLocalImage,pinnedMetadataDocker} from './static_web_evaluation_host_bridge.mjs';
import {pilotHostTools,pilotLocalRoot} from './windows_pilot_lifecycle_qualification.mjs';
import {readPilotAttempt} from './static_web_pilot_attempt_journal.mjs';
import {readDispatchPins} from './static_web_pilot_evidence.mjs';
import {prepareReviewedCandidateTransport} from './static_web_candidate_transport.mjs';
const fail=code=>{throw Error(code);};
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const receipt=/^windows-static-web-[0-9a-f]{12}\.json$/;
export function previewLocalCandidateTransport(request,{platform=process.platform,env=process.env,docker,
  readReceipt=readLocalReceipt}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('PILOT_LOCAL_WINDOWS_ONLY');
  if(!exact(request,['operation','trustedReceiptName','receiptNames','currentScope','criteria','reviewedContract','archiveBase64','selection'])||
    request.operation!=='prepare-candidate-transport'||typeof request.trustedReceiptName!=='string'||!receipt.test(request.trustedReceiptName)||
    !Array.isArray(request.receiptNames)||![4,5,7].includes(request.receiptNames.length)||
    new Set([request.trustedReceiptName,...request.receiptNames]).size!==request.receiptNames.length+1||
    !request.receiptNames.every(x=>typeof x==='string'&&receipt.test(x))||
    typeof request.archiveBase64!=='string'||request.archiveBase64.length>2800000||
    !/^[A-Za-z0-9+/]+={0,2}$/.test(request.archiveBase64))
    fail('CANDIDATE_TRANSPORT_REQUEST_INVALID');
  const archive=Buffer.from(request.archiveBase64,'base64');
  if(archive.length>2*1024*1024||archive.toString('base64')!==request.archiveBase64)
    fail('CANDIDATE_TRANSPORT_REQUEST_INVALID');
  const root=pilotLocalRoot(env);
  const host=pilotHostTools(docker);
  if(readPilotAttempt(root)!==null||host.list('label=codingassistant.static-web-pilot-qualification')!=='')
    fail('PILOT_PRIOR_CLEANUP_REQUIRED');
  const engineId=host.engine(),imageMetadata=readLocalImage({platform,env,docker});
  const [working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions]=request.receiptNames.map(name=>readReceipt(name,env));
  const inputs={currentScope:request.currentScope,selection:request.currentScope,archive,criteria:request.criteria,
    reviewedContract:request.reviewedContract,qualificationReport:readReceipt(request.trustedReceiptName,env),
    currentImageMetadata:imageMetadata};
  const {descriptor}=prepareReviewedCandidateTransport(inputs,
    {working,negatives,interruptions,boundary,build,dispatch,pinnedInterruptions,imageMetadata,engineId,root},request.selection,
    request.receiptNames.length===7?readDispatchPins(env):undefined);
  if(host.engine()!==engineId||host.list('label=codingassistant.static-web-pilot-qualification')!==''||readPilotAttempt(root)!==null)
    fail('PILOT_HOST_CHANGED');
  return descriptor;
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('CANDIDATE_TRANSPORT_PREVIEW NOT_RUN');return 0;}
  try{
    if(argv.length!==1||argv[0]!=='--stdin')fail('CANDIDATE_TRANSPORT_REQUEST_INVALID');
    const buffer=Buffer.alloc(3*1024*1024+1);let size=0;
    while(size<buffer.length){const n=fs.readSync(0,buffer,size,buffer.length-size,null);if(!n)break;size+=n;}
    if(size>=buffer.length)fail('CANDIDATE_TRANSPORT_REQUEST_INVALID');
    const request=JSON.parse(buffer.subarray(0,size).toString('utf8'));
    console.log(JSON.stringify(previewLocalCandidateTransport(request,{docker:pinnedMetadataDocker()})));
    return 0;
  }catch(e){
    console.log(JSON.stringify({status:'BLOCKED',reason:/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'CANDIDATE_TRANSPORT_BLOCKED',
      executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false}));return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
