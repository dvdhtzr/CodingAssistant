// Fixed reviewed candidate-shaped fixture only. Embedded by the dedicated
// qualification host. Never accept project input, paths, arguments or grants.
import {spawnSync} from 'node:child_process';
import {observeCandidate} from './static_web_candidate_lifecycle.mjs';
import {security} from './windows_aet8_trusted_guest.mjs';
import {runFixedCompatibility,CASES} from './windows_chromium_sandbox_guest.mjs';
const INPUT='__CA_FIXED_CANDIDATE_INPUT__';
const fixture=JSON.parse(Buffer.from(INPUT,'base64').toString('utf8'));
const progress=stage=>process.stdout.write(JSON.stringify({kind:'progress',stage})+'\n');
const timer=setTimeout(()=>{progress('DEADLINE');process.exit(2);},145000);
try{
  if(process.platform!=='linux'||process.getuid?.()!==1000||!process.argv.includes('--run-fixed-candidate-fixture'))throw Error('FIXTURE_GUEST_REQUIRED');
  progress('START');
  const observed=await observeCandidate(fixture,{
    securityProbe:async()=>{progress('SECURITY');return security();},
    buildProbe(assets,files){
      progress('BUILD');
      // Fixed fixture only: parse JS without executing it; check the
      // exact linked JS files using the same static asset resolver.
      let javascript_syntax=true,local_assets_linked=true;
      const linked=new Set();
      for(const file of files){
        const response=assets.resolve('GET','/'+file.path);
        if(response.status!==200)throw Error('FIXTURE_ASSET_MISSING');
        if(file.path.endsWith('.js')){
          const child=spawnSync(process.execPath,['--check','-'],{
            input:response.body,encoding:'utf8',timeout:4000,maxBuffer:8192,
            env:{HOME:'/tmp',PATH:process.env.PATH}
          });
          if(child.error||child.signal||!Number.isInteger(child.status))
            throw Error('FIXTURE_SYNTAX_PROBE_UNAVAILABLE');
          if(child.status!==0)javascript_syntax=false;
        }
        if(file.path.endsWith('.html')){
          for(const m of response.body.matchAll(/<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["']/gi)){
            const target=m[1].replace(/^\.\//,'');
            if(assets.resolve('GET','/'+target).status!==200)local_assets_linked=false;
            linked.add(target);
          }
        }
      }
      if(files.some(f=>f.path.endsWith('.js')&&!linked.has(f.path)))local_assets_linked=false;
      return {javascript_syntax,local_assets_linked};
    },
    async sandboxProbe(){
      progress('SANDBOX');
      const result=await runFixedCompatibility();
      return CASES.every(k=>result.cases[k]===true);
    }
  });
  progress('COMPLETE');
  process.stdout.write(observed.raw+'\n',()=>process.exit(0));
}catch{
  progress('BLOCKED');process.exitCode=2;
}finally{clearTimeout(timer);}
