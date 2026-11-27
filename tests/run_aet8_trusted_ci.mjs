// Linux CI only: actual trusted guest regression, not Windows host qualification.
// Same fixed sources, seccomp, resource bounds, no mounts and private browser
// pipe as the owner runner. No Docker context override or generated input.
import {randomBytes} from 'node:crypto';
import {IMAGE_TAG,shellDocker,validatePinnedImage} from '../tools/m4/windows_docker_qualification.mjs';
import {trustedDockerArgs,trustedPayload,parseGuestStream} from '../tools/m4/windows_aet8_trusted_qualification.mjs';

if(process.platform!=='linux'||process.env.GITHUB_ACTIONS!=='true'||process.argv.length!==2||process.env.DOCKER_HOST||process.env.DOCKER_CONTEXT)
  throw Error('CI Linux only; no context override');
const name='ca-win-aet8-'+randomBytes(6).toString('hex');
const inspect=shellDocker(['image','inspect',IMAGE_TAG,'--format','{{json .}}']);
if(inspect.status!==0)throw Error('CI cached image missing');
const pin=validatePinnedImage(JSON.parse(inspect.stdout));
const payload=trustedPayload();
let ok=false,removed=false;
const absent=()=>{
  const r=shellDocker(['ps','-a','--filter','name=^/'+name+'$','--format','{{.ID}}']);
  return r.status===0&&r.stdout.trim()==='';
};
if(!absent())throw Error('CI container name collision');
try{
  const execution=shellDocker(trustedDockerArgs(pin.imageId,name),180000,payload.source);
  const {checkpoint,final}=parseGuestStream(execution.stdout||'');
  const x=final??checkpoint;
  // Only reviewed bounded observations, never raw browser stderr or DOM.
  console.log(JSON.stringify({kind:'CI_TRUSTED_FIXTURE_ONLY',timedOut:execution.timedOut,exit:execution.status,lastStage:checkpoint?.stage,security:x?.security,fixture:x?.fixture,observations:x?.observations,failure:final?.failure,generatedCodeExecutionEnabled:false,runtimeQualified:false}));
  ok=!execution.timedOut&&execution.status===0&&!!final&&!final.failure&&Object.values(final.security).every(Boolean)&&Object.values(final.fixture).every(Boolean);
}finally{
  const found=shellDocker(['container','inspect',name,'--format','{{json .Config.Labels}}']);
  if(found.status===0&&JSON.parse(found.stdout)?.['codingassistant.aet8']===name)shellDocker(['rm','-f',name],15000);
  removed=absent();
  console.log('CI trusted fixture container removed: '+removed);
}
if(!ok||!removed)process.exitCode=2;
