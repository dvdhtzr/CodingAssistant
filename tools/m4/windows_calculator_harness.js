// Trusted, independently reviewed M4.4 browser acceptance harness.
// Served by the fixed guest on 127.0.0.1, never submitted by a model.
(async()=>{
  const stage=document.body.dataset.stage;
  const result={};
  let frame=null;
  const set=(name,value)=>{result[name]=value===true;};
  async function open(file){
    if(frame)frame.remove();
    frame=document.createElement('iframe');
    frame.src='/'+file;
    const loaded=new Promise((resolve,reject)=>{
      const timeout=setTimeout(()=>reject(Error('frame timeout')),4000);
      frame.addEventListener('load',()=>{clearTimeout(timeout);resolve();},{once:true});
    });
    document.body.append(frame);
    await loaded;
    return frame.contentDocument;
  }
  function click(doc,selector){
    const element=doc.querySelector(selector);
    if(!element)throw Error('missing control');
    element.click();
  }
  function keys(doc,input){
    for(const key of input)
      doc.dispatchEvent(new frame.contentWindow.KeyboardEvent('keydown',{
        key,bubbles:true,cancelable:true,
      }));
  }
  function equals(doc){keys(doc,['Enter']);}
  function value(doc){return doc.querySelector('#display')?.textContent;}
  const fresh=()=>localStorage.removeItem('ca-calculation-journal-reference-v1');
  try{
    if(stage==='first'){
      fresh();
      let doc=await open('broken.html');
      click(doc,'[data-number="2"]');
      click(doc,'[data-action="equals"]');
      set('negative_inert_browser',value(doc)==='0' &&
        ![...doc.querySelectorAll('script[src]')].some(x=>x.src.includes('broken.js')));
      fresh();
      doc=await open('working.html');
      click(doc,'[data-number="2"]');
      click(doc,'[data-operation="+"]');
      click(doc,'[data-number="3"]');
      click(doc,'[data-action="equals"]');
      set('mouse_2_plus_3_equals_5',value(doc)==='5');
      fresh();
      doc=await open('working.html');keys(doc,'12.5/2');equals(doc);
      set('keyboard_12_5_divided_by_2',value(doc)==='6.25');
      fresh();
      doc=await open('working.html');keys(doc,'0.1+0.2');equals(doc);
      set('decimal_0_1_plus_0_2',value(doc)==='0.3');
      fresh();
      doc=await open('working.html');
      keys(doc,'19');keys(doc,['Backspace']);
      const afterBack=value(doc)==='1';
      click(doc,'[data-action="clear"]');
      keys(doc,'9/0');equals(doc);
      set('backspace_and_divide_by_zero',
        afterBack&&value(doc)==='Cannot divide by zero');
      fresh();
      doc=await open('working.html');
      keys(doc,'8+2');equals(doc);
      click(doc,'[data-action="clear"]');
      click(doc,'[data-action="back"]');
      const back=value(doc)==='10';
      click(doc,'[data-action="clear"]');
      keys(doc,'7*3');equals(doc);
      click(doc,'[data-action="history"]');
      const history=doc.querySelectorAll('#history-list li').length===2;
      click(doc,'[data-history-index="1"]');
      set('back_and_history_recall',back&&history&&value(doc)==='10');
      fresh();
      doc=await open('working.html');keys(doc,'2+3');equals(doc);
      doc=await open('working.html');
      click(doc,'[data-action="history"]');
      set('history_survives_page_reload',
        doc.querySelectorAll('#history-list li').length===1 &&
        doc.querySelector('#history-list')?.textContent.includes('2 + 3 = 5')===true);
    }else if(stage==='restart'){
      const doc=await open('working.html');
      click(doc,'[data-action="history"]');
      const item=doc.querySelector('#history-list [data-history-index="0"]');
      const saw=item?.textContent==='2 + 3 = 5';
      item?.click();
      set('history_survives_browser_restart',saw&&value(doc)==='5');
    }
  }catch{
    // Incomplete/missing positive tests become false in the independent guest.
  }
  document.getElementById('ca-m44-result').textContent=
    JSON.stringify({stage,tests:result});
})();
