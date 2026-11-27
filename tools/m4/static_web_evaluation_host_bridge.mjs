// Trusted control-plane bridge, planning only. No generated source is
// extracted, executed, served, or mounted. Docker commands are metadata
// queries only. The local qualification file is evidence, not attestation.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {prepareEvaluationAdmission,bindTrustedFixtureReceipt} from './static_web_evaluation_admission.mjs';
import {IMAGE_TAG,shellDocker,validateDockerContext,validatePinnedImage} from './windows_docker_qualification.mjs';

const fail=code=>{throw Error(code);};
const exact=(x,keys)=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.keys(x).length===keys.length&&keys.every(k=>Object.hasOwn(x,k));
const receiptName=/^windows-static-web-[0-9a-f]{12}\.json$/;
const maxInput=3*1024*1024;
export function readLocalReceipt(name,env=process.env){
  if(typeof name!=='string'||!receiptName.test(name)||!env.LOCALAPPDATA||
    !path.isAbsolute(env.LOCALAPPDATA))fail('EVALUATION_RECEIPT_NAME_INVALID');
  const root=path.join(env.LOCALAPPDATA,'CodingAssistant','qualifications');
  const file=path.join(root,name);
  try{
    // The directory must be the normal owner-local location, not a
    // symlink/junction redirect. This is not an OS attestation boundary.
    const normalize=p=>path.resolve(p).toLowerCase();
    if(normalize(fs.realpathSync(root))!==normalize(root)||
      normalize(fs.realpathSync(file))!==normalize(file)||
      !fs.lstatSync(file).isFile())fail('EVALUATION_RECEIPT_UNAVAILABLE');
    const fd=fs.openSync(file,'r');
    try{
      const st=fs.fstatSync(fd);
      if(!st.isFile()||st.size<2||st.size>65536)fail('EVALUATION_RECEIPT_INVALID');
      const buffer=Buffer.alloc(st.size+1);
      const size=fs.readSync(fd,buffer,0,buffer.length,null);
      if(size!==st.size)fail('EVALUATION_RECEIPT_INVALID');
      return JSON.parse(buffer.subarray(0,size).toString('utf8'));
    }finally{fs.closeSync(fd);}
  }catch{fail('EVALUATION_RECEIPT_UNAVAILABLE');}
}

export function readLocalImage({platform=process.platform,env=process.env,docker=shellDocker}={}){
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)
    fail('EVALUATION_LOCAL_WINDOWS_ONLY');
  const query=args=>{
    const response=docker(args,8000);
    if(response.status!==0||response.timedOut||typeof response.stdout!=='string'||
      response.stdout.length<1||response.stdout.length>65536)
      fail('EVALUATION_DOCKER_METADATA_UNAVAILABLE');
    return response.stdout;
  };
  const json=args=>{
    try{return JSON.parse(query(args));}
    catch{fail('EVALUATION_DOCKER_METADATA_UNAVAILABLE');}
  };
  const context=query(['context','show']).trim();
  if(!/^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,70}$/.test(context))
    fail('EVALUATION_DOCKER_CONTEXT_INVALID');
  try{
    const endpoint=json(['context','inspect',context,'--format','{{json .}}'])?.Endpoints?.docker?.Host;
    if(!/^npipe:\/\/\/\/\.\/pipe\/(dockerDesktopLinuxEngine|docker_engine)$/i.test(endpoint??''))
      fail('EVALUATION_DOCKER_CONTEXT_INVALID');
    validateDockerContext(context,endpoint,json(['info','--format','{{json .}}']));
    const image=json(['image','inspect',IMAGE_TAG,'--format','{{json .}}']);
    validatePinnedImage(image);
    return image;
  }catch{fail('EVALUATION_DOCKER_BINDING_INVALID');}
}

export function prepareLocalEvaluation(request,options={}){
  const {platform=process.platform,env=process.env,docker=shellDocker,readReceipt=readLocalReceipt}=options;
  if(platform!=='win32'||env.DOCKER_HOST||env.DOCKER_CONTEXT)fail('EVALUATION_LOCAL_WINDOWS_ONLY');
  if(!request||typeof request.receiptName!=='string'||!receiptName.test(request.receiptName))
    fail('EVALUATION_RECEIPT_NAME_INVALID');
  const inspection=request.operation==='inspect-receipt';
  if(inspection){
    if(!exact(request,['operation','receiptName']))fail('EVALUATION_HOST_REQUEST_INVALID');
  }else{
    if(request.operation!=='prepare-admission'||
      !exact(request,['operation','receiptName','currentScope','criteria','reviewedContract','archiveBase64'])||
      typeof request.archiveBase64!=='string'||request.archiveBase64.length>2800000||
      !/^[A-Za-z0-9+/]+={0,2}$/.test(request.archiveBase64))
      fail('EVALUATION_HOST_REQUEST_INVALID');
  }
  const report=readReceipt(request.receiptName,env);
  const metadata=readLocalImage({platform,env,docker});
  if(inspection)return {status:'TRUSTED_FIXTURE_BINDING_INSPECTED',qualification:bindTrustedFixtureReceipt(report,metadata),
    executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false};
  const archive=Buffer.from(request.archiveBase64,'base64');
  if(archive.toString('base64')!==request.archiveBase64||archive.length>2*1024*1024)
    fail('EVALUATION_HOST_REQUEST_INVALID');
  return prepareEvaluationAdmission({currentScope:request.currentScope,selection:request.currentScope,
    archive,criteria:request.criteria,reviewedContract:request.reviewedContract,
    qualificationReport:report,currentImageMetadata:metadata});
}
function readStdin(){
  const chunks=[];let size=0;
  const buffer=Buffer.alloc(16384);
  for(;;){
    const count=fs.readSync(0,buffer,0,buffer.length,null);
    if(!count)break;
    size+=count;if(size>maxInput)fail('EVALUATION_HOST_REQUEST_TOO_LARGE');
    chunks.push(Buffer.from(buffer.subarray(0,count)));
  }
  try{return JSON.parse(Buffer.concat(chunks).toString('utf8'));}
  catch{fail('EVALUATION_HOST_REQUEST_INVALID');}
}
export function main(argv=process.argv.slice(2)){
  if(argv.length===0){console.log('EVALUATION_HOST_BRIDGE NOT_RUN');return 0;}
  try{
    const request=argv.length===1&&argv[0]==='--stdin'?readStdin():
      argv.length===2&&argv[0]==='--inspect-receipt'?{operation:'inspect-receipt',receiptName:argv[1]}:null;
    if(!request)fail('EVALUATION_HOST_REQUEST_INVALID');
    console.log(JSON.stringify(prepareLocalEvaluation(request)));return 0;
  }catch(e){
    const reason=/^[A-Z_]{3,90}$/.test(e?.message??'')?e.message:'EVALUATION_HOST_BLOCKED';
    console.log(JSON.stringify({status:'BLOCKED',reason,executionEnabled:false,runtimeQualified:false,
      generatedArtifactVerified:false}));return 2;
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))process.exitCode=main();
