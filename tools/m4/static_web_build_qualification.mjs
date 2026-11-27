// New build-qualified adapter template. Older fixture bundles and evidence
// remain byte-identical. Only frozen controls can be run by the qualifier.
import fs from 'node:fs';
import {createHash} from 'node:crypto';
import {fixedCandidateBundle,fixedCandidateFixture} from './windows_candidate_fixture_qualification.mjs';
import {readPinnedSources} from './windows_calculator_evaluator_qualification.mjs';
import {createCandidateAssets} from './static_web_candidate_assets.mjs';
const sha=x=>createHash('sha256').update(x).digest('hex');
const fail=code=>{throw Error(code);};
const marker='__CA_FIXED_CANDIDATE_INPUT__';
export const BUILD_CONTROLS=Object.freeze(['build-working','build-broken','build-spoof','build-missing-asset','build-invalid-syntax']);
export function buildQualifiedCandidateBundle(){
  const prior=fixedCandidateBundle();
  const location=new URL('./static_web_candidate_build.mjs',import.meta.url);
  const st=fs.lstatSync(location);
  if(!st.isFile()||st.isSymbolicLink()||st.size>32000)fail('BUILD_ADAPTER_MODULE_INVALID');
  const build=fs.readFileSync(location,'utf8').replace(/\r\n/g,'\n');
  const start='    buildProbe(assets,files){',end='    async sandboxProbe(){';
  if(prior.source.split(start).length!==2||prior.source.split(end).length!==2)fail('BUILD_ADAPTER_TEMPLATE_CHANGED');
  const from=prior.source.indexOf(start),to=prior.source.indexOf(end);
  if(to<=from)fail('BUILD_ADAPTER_TEMPLATE_CHANGED');
  // vm.Script compiles classic script syntax but is never run. The
  // only subprocess program is this constant trusted parser; candidate
  // bytes are stdin data. A top-level return is invalid in a browser.
  const checker="const fs=require('node:fs'),vm=require('node:vm');try{new vm.Script(fs.readFileSync(0,'utf8'));}catch(e){process.exitCode=e instanceof SyntaxError?1:2;}";
  const replacement="    buildProbe(assets,files){\n"+
    "      progress('BUILD');\n"+
    "      return inspectCandidateBuild(assets,files,content=>{\n"+
    "        const child=spawnSync(process.execPath,['--input-type=commonjs','-e',"+JSON.stringify(checker)+"],{\n"+
    "          input:content,encoding:'utf8',timeout:4000,maxBuffer:8192,env:{HOME:'/tmp',PATH:process.env.PATH}});\n"+
    "        if(child.error||child.signal||![0,1].includes(child.status))throw Error('CANDIDATE_SYNTAX_PROBE_UNAVAILABLE');\n"+
    "        return child.status===0;\n"+
    "      });\n    },\n";
  const source="import {inspectCandidateBuild} from 'data:text/javascript;base64,"+
    Buffer.from(build).toString('base64')+"';\n"+prior.source.slice(0,from)+replacement+prior.source.slice(to);
  return Object.freeze({source,adapterSha256:sha(source),evaluatorSha256:prior.evaluatorSha256,buildSha256:sha(build)});
}
export function buildQualificationFixture(name,bundle,policySha256,attemptId){
  if(!BUILD_CONTROLS.includes(name)||typeof attemptId!=='string'||! /^[a-f0-9]{32}$/.test(attemptId)||attemptId.length!==32)
    fail('BUILD_CONTROL_INVALID');
  if(['build-working','build-broken','build-spoof'].includes(name))
    return fixedCandidateFixture(name.slice(6),bundle,policySha256,attemptId);
  const original=readPinnedSources();
  const html=original['working.html'].toString('utf8');
  const content=name==='build-missing-asset'?[['index.html',html]]:
    [['index.html',html],['working.js','return 1;']];
  const files=content.map(([path,content])=>({path,sha256:sha(content),utf8Bytes:Buffer.byteLength(content),content}));
  const manifest=files.map(({content,...f})=>f);
  const sourceFingerprint=sha(manifest.map(f=>f.path+'\t'+f.sha256).join('\n'));
  const archiveSha256=sha(JSON.stringify({kind:name,files})),candidateSha256=sha(JSON.stringify({name,archiveSha256,sourceFingerprint}));
  const binding={admissionSha256:sha('build-control:'+candidateSha256),candidateSha256,archiveSha256,sourceFingerprint,files:manifest};
  const payloadJson=JSON.stringify({schemaVersion:1,...binding,files});
  createCandidateAssets(payloadJson,binding);
  const expected={attemptId,candidateSha256,archiveSha256,evaluatorSha256:bundle.evaluatorSha256,
    adapterSha256:bundle.adapterSha256,policySha256};
  if(bundle.source.split(marker).length!==2)fail('BUILD_ADAPTER_TEMPLATE_CHANGED');
  return {expected,source:bundle.source.replace(marker,()=>Buffer.from(JSON.stringify({payloadJson,binding,expected})).toString('base64'))};
}
export function buildControlMatches(name,o){
  if(!BUILD_CONTROLS.includes(name)||!o)return false;
  if(name==='build-working'||name==='build-spoof')
    return o.status===(name==='build-working'?'CHECKS_PASSED_NOT_VERIFIED':'TESTS_FAILED')&&
      o.completed===7&&o.passed===(name==='build-working'?7:0);
  return o.status==='BUILD_FAILED'&&o.completed===0&&o.passed===0&&
    o.build?.javascript_syntax===(name!=='build-invalid-syntax')&&
    o.build?.local_assets_linked===(name==='build-invalid-syntax')&&
    Object.values(o.infrastructure??{}).length===4&&Object.values(o.infrastructure).every(v=>v===null);
}
