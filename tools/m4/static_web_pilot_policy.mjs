// Separate owner-approved qualification policy. This module constructs
// and audits container configuration; it cannot create/start containers,
// accept generated source, grant execution or promote the registry.
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {checkedChromiumSeccomp,FIXTURE_PID_LIMIT} from './windows_chromium_sandbox_qualification.mjs';
import {validatePinnedImage} from './windows_docker_qualification.mjs';
import {CALCULATION_CONTRACT_VERSION} from './calculation_journal_contract.mjs';

const sha=value=>createHash('sha256').update(value).digest('hex');
const validDigest=x=>typeof x==='string'&&x.length===64&&/^[a-f0-9]{64}$/.test(x);
const validId=x=>typeof x==='string'&&x.length===32&&/^[a-f0-9]{32}$/.test(x);
const fail=code=>{throw Error(code);};
const canonical=x=>Array.isArray(x)?x.map(canonical):x&&typeof x==='object'?
  Object.fromEntries(Object.keys(x).sort().map(k=>[k,canonical(x[k])])):x;
const equal=(a,b)=>JSON.stringify(canonical(a))===JSON.stringify(canonical(b));
const empty=x=>x==null||Array.isArray(x)&&x.length===0||typeof x==='object'&&!Array.isArray(x)&&Object.keys(x).length===0;
const NAME_PREFIX='ca-win-pilot-';
const LABEL='codingassistant.static-web-pilot-qualification';
const ENTRY='--run-fixed-candidate-fixture';
const TMPFS='rw,nosuid,nodev,mode=1777,size=256m';
const ENV=Object.freeze(['HOME=/tmp','XDG_CONFIG_HOME=/tmp/config','XDG_CACHE_HOME=/tmp/cache']);
const cleanEnv=values=>{
  if(!Array.isArray(values)||values.some(x=>typeof x!=='string'||!x.includes('=')))fail('PILOT_ENVIRONMENT_INVALID');
  const result=Object.create(null);
  for(const value of values){
    const i=value.indexOf('='),name=value.slice(0,i);
    if(!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)||Object.hasOwn(result,name)||
      /(?:TOKEN|SECRET|PASSWORD|CREDENTIAL|API_KEY|OLLAMA_HOST|DOCKER_HOST|DOCKER_CONTEXT|NODE_OPTIONS)/i.test(name))
      fail('PILOT_ENVIRONMENT_INVALID');
    result[name]=value.slice(i+1);
  }
  return result;
};
export const PILOT_POLICY_ID='static-web-calculation-journal-windows-qualification-v1';
export function preparePilotPolicy(imageMetadata,adapterSha256,evaluatorSha256){
  if(!validDigest(adapterSha256)||!validDigest(evaluatorSha256)||FIXTURE_PID_LIMIT!==256)
    fail('PILOT_POLICY_INVALID');
  const image=validatePinnedImage(imageMetadata);
  const imageConfig=imageMetadata.Config;
  if(!imageConfig||!empty(imageConfig.Volumes)||!empty(imageConfig.ExposedPorts))
    fail('PILOT_IMAGE_CONFIG_UNSAFE');
  const environment={...cleanEnv(imageConfig.Env??[]),...cleanEnv(ENV)};
  const seccomp=checkedChromiumSeccomp();
  const seccompPolicy=JSON.parse(fs.readFileSync(seccomp.path,'utf8'));
  // Independently bind canonical profile bytes at construction; no
  // arbitrary seccomp path or profile can be supplied by a caller.
  if(sha(JSON.stringify(seccompPolicy,null,2)+'\n')!==seccomp.sha256)
    fail('PILOT_SECCOMP_CHANGED');
  const descriptor=Object.freeze({schemaVersion:1,policyId:PILOT_POLICY_ID,
    contractId:CALCULATION_CONTRACT_VERSION,imageId:image.imageId,registryDigest:image.registryDigest,
    adapterSha256,evaluatorSha256,seccompSha256:seccomp.sha256,
    environmentSha256:sha(JSON.stringify(canonical(environment))),
    pidLimit:256,memoryBytes:2147483648,nanoCpus:2000000000,shmBytes:268435456,
    scratchBytes:268435456,guestDeadlineSeconds:145,hostDeadlineSeconds:180,
    user:'1000:1000',network:'none',ipc:'private',rootReadOnly:true,capabilities:'none',
    noNewPrivileges:true,init:true,autoRemove:false,hostMounts:0,entrypoint:'node',entryFlag:ENTRY});
  const policySha256=sha(JSON.stringify(descriptor));
  function identity(attemptId){
    if(!validId(attemptId))fail('PILOT_ATTEMPT_INVALID');
    return Object.freeze({attemptId,name:NAME_PREFIX+attemptId,
      label:LABEL,policySha256,imageId:image.imageId});
  }
  function createArgs(attemptId){
    const item=identity(attemptId);
    if(checkedChromiumSeccomp().sha256!==descriptor.seccompSha256)fail('PILOT_SECCOMP_CHANGED');
    // Explicit create -> inspect -> start. No automatic removal because
    // the owner must independently reconcile an exact container ID.
    return ['create','-i','--pull=never','--name',item.name,
      '--label',LABEL+'='+attemptId,'--label','codingassistant.pilot-policy='+policySha256,
      '--init','--network=none','--read-only','--cap-drop=all',
      '--security-opt=no-new-privileges','--security-opt=seccomp='+seccomp.path,
      '--user=1000:1000','--pids-limit=256','--memory=2g','--memory-swap=2g','--cpus=2',
      '--ipc=private','--shm-size=256m','--tmpfs=/tmp:'+TMPFS,
      ...ENV.map(x=>'--env='+x),'--entrypoint=node',image.imageId,
      '--input-type=module','-',ENTRY];
  }
  function audit(attemptId,containerId,inspect){
    const item=identity(attemptId),config=inspect?.Config,host=inspect?.HostConfig;
    if(!validDigest(containerId)||inspect?.Id!==containerId||inspect?.Name!=='/'+item.name||
      inspect.Image!==image.imageId||config?.Image!==image.imageId||
      config?.Labels?.[LABEL]!==attemptId||config?.Labels?.['codingassistant.pilot-policy']!==policySha256)
      fail('PILOT_CONTAINER_IDENTITY_MISMATCH');
    if(!host||inspect.State?.Status!=='created'||inspect.State?.Running!==false||
      config.User!==descriptor.user||config.OpenStdin!==true||config.Tty!==false||
      !equal(config.Entrypoint,['node'])||!equal(config.Cmd,['--input-type=module','-',ENTRY])||
      !empty(config.Volumes)||!empty(config.ExposedPorts)||
      !equal(cleanEnv(config.Env),environment))fail('PILOT_CONTAINER_CONFIGURATION_MISMATCH');
    if(host.NetworkMode!=='none'||host.IpcMode!=='private'||!['','private'].includes(host.PidMode)||
      !['','private'].includes(host.UTSMode)||host.Privileged!==false||
      host.ReadonlyRootfs!==true||host.Init!==true||host.AutoRemove!==false||
      host.Memory!==descriptor.memoryBytes||host.MemorySwap!==descriptor.memoryBytes||
      host.NanoCpus!==descriptor.nanoCpus||host.PidsLimit!==descriptor.pidLimit||host.ShmSize!==descriptor.shmBytes||
      !equal((host.CapDrop??[]).map(x=>x.toLowerCase()),['all'])||!empty(host.CapAdd)||
      !empty(host.Binds)||!empty(host.Mounts)||!empty(host.VolumesFrom)||!empty(host.Devices)||
      !empty(host.DeviceRequests)||!empty(host.DeviceCgroupRules)||!empty(host.PortBindings)||
      host.PublishAllPorts!==false||!empty(host.ExtraHosts)||!empty(host.Links)||
      !empty(host.Dns)||!empty(host.DnsOptions)||!empty(host.DnsSearch)||
      !['','no'].includes(host.RestartPolicy?.Name)||
      !equal(host.Tmpfs,{'/tmp':TMPFS}))fail('PILOT_CONTAINER_ISOLATION_MISMATCH');
    if(!Array.isArray(inspect.Mounts)||inspect.Mounts.some(m=>m.Type!=='tmpfs'||m.Destination!=='/tmp'||m.Source))
      fail('PILOT_CONTAINER_ISOLATION_MISMATCH');
    const security=host.SecurityOpt;
    if(!Array.isArray(security)||security.length!==2||
      !security.some(x=>x==='no-new-privileges'||x==='no-new-privileges:true'))
      fail('PILOT_CONTAINER_SECCOMP_MISMATCH');
    const encoded=security.find(x=>typeof x==='string'&&x.startsWith('seccomp='));
    let actual;try{actual=JSON.parse(encoded?.slice(8)??'');}catch{fail('PILOT_CONTAINER_SECCOMP_MISMATCH');}
    if(!equal(actual,seccompPolicy))fail('PILOT_CONTAINER_SECCOMP_MISMATCH');
    return Object.freeze({...item,containerId,status:'CONTAINER_CONFIGURATION_AUDITED',
      executionEnabled:false,runtimeQualified:false,generatedArtifactVerified:false});
  }
  return Object.freeze({descriptor,policySha256,identity,createArgs,audit});
}
