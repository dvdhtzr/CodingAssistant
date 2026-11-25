// AET-8: independent task-specific functional checks. Trusted driver only.
// No browser launch, generated-source admission, Docker policy or execution
// permission lives here. The caller owns the browser and evidence lifecycle.
// Never run this code as a script in the application page.
export const CALCULATION_CONTRACT_VERSION='calculation-journal-v1';
export const CALCULATION_CASES=Object.freeze([
  'mouse_2_plus_3_equals_5','keyboard_12_5_divided_by_2',
  'decimal_0_1_plus_0_2','backspace_and_divide_by_zero',
  'back_and_history_recall','history_survives_page_reload',
  'history_survives_browser_restart',
]);
const click=(page,name)=>page.getByRole('button',{name,exact:true}).click({timeout:3000});
const result=async page=>(await page.locator('output#display').innerText({timeout:3000})).trim();
const type=async(page,value)=>{await page.keyboard.type(value);await page.keyboard.press('Enter');};
const history=page=>page.getByRole('region',{name:'Calculation history',exact:true});
const count=async page=>await history(page).locator('li').count();
const recall=(page,name)=>history(page).getByRole('button',{name,exact:true}).click({timeout:3000});
const run=async(open,check,onCaseError)=>{
  let session;
  try{
    session=await open();
    return await check(session.page,session)===true;
  }catch(error){onCaseError?.(error);return false;}
  finally{if(session)await session.close();}
};
// openFresh is a trusted fixture driver returning {page,close,restart}.
// A restart must create an actual new browser process with the same private
// profile. The candidate page never supplies a test name or result record.
export async function evaluateCalculationJournal(openFresh,{onCaseError}={}){
  if(typeof openFresh!=='function')throw Error('Trusted driver required');
  const checks={};
  const runCase=check=>run(openFresh,check,onCaseError);
  checks.mouse_2_plus_3_equals_5=await runCase(async p=>{
    if(await result(p)!=='0')return false;
    for(const key of ['2','Add','3','Equals'])await click(p,key);
    return await result(p)==='5';
  });
  checks.keyboard_12_5_divided_by_2=await runCase(async p=>{
    if(await result(p)!=='0')return false;
    await type(p,'12.5/2');return await result(p)==='6.25';
  });
  checks.decimal_0_1_plus_0_2=await runCase(async p=>{
    if(await result(p)!=='0')return false;
    await type(p,'0.1+0.2');return await result(p)==='0.3';
  });
  checks.backspace_and_divide_by_zero=await runCase(async p=>{
    await p.keyboard.type('19');await p.keyboard.press('Backspace');
    if(await result(p)!=='1')return false;
    await p.keyboard.type('2');if(await result(p)!=='12')return false;
    await click(p,'Clear');await type(p,'9/0');
    if(await result(p)!=='Cannot divide by zero')return false;
    await click(p,'Clear');if(await result(p)!=='0')return false;
    await type(p,'6/2');return await result(p)==='3';
  });
  checks.back_and_history_recall=await runCase(async p=>{
    await type(p,'8+2');if(await result(p)!=='10')return false;
    await click(p,'Clear');await click(p,'Back recall');
    if(await result(p)!=='10')return false;
    await click(p,'Clear');await type(p,'7*3');
    if(await result(p)!=='21')return false;
    await click(p,'History');if(await count(p)!==2)return false;
    await recall(p,'8 + 2 = 10');return await result(p)==='10';
  });
  checks.history_survives_page_reload=await runCase(async p=>{
    await type(p,'2+3');if(await result(p)!=='5')return false;
    await p.reload();await click(p,'History');
    if(await count(p)!==1)return false;
    await recall(p,'2 + 3 = 5');return await result(p)==='5';
  });
  checks.history_survives_browser_restart=await runCase(async(p,s)=>{
    await type(p,'2+3');if(await result(p)!=='5'||typeof s.restart!=='function')return false;
    const reopened=await s.restart();
    await click(reopened,'History');if(await count(reopened)!==1)return false;
    await recall(reopened,'2 + 3 = 5');return await result(reopened)==='5';
  });
  return Object.freeze({contract:CALCULATION_CONTRACT_VERSION,
    checks:Object.freeze(checks),passed:CALCULATION_CASES.filter(k=>checks[k]===true).length,
    required:CALCULATION_CASES.length,verificationStatus:'EVALUATOR_OBSERVATIONS_ONLY',
    runtimeQualified:false,generatedArtifactVerified:false});
}
