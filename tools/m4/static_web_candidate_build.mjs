// Deliberately restricted Calculation Journal markup/asset subset.
// Not a general HTML parser, JavaScript sandbox, or proof of safety.
// Reject unsupported markup rather than guessing browser interpretation.
const tags=new Set(['html','head','body','title','meta','link','style','script','main','header','footer',
  'section','article','nav','aside','div','span','h1','h2','h3','h4','h5','h6','p','output',
  'button','ol','ul','li','strong','em','small','label','br','hr','kbd','code']);
const voids=new Set(['meta','link','br','hr']);
const common=new Set(['id','class','lang','dir','role','hidden','title','tabindex']);
const extra={
  meta:new Set(['charset','name','content']),link:new Set(['rel','href']),
  script:new Set(['src','defer','type']),button:new Set(['type','disabled','name','value']),
  output:new Set(['for','name']),label:new Set(['for']),ol:new Set(['start','reversed']),
};
const bools=new Set(['hidden','disabled','defer','reversed']);
const pathPattern=/^(?:[A-Za-z0-9_-]+\/){0,3}[A-Za-z0-9_-]+\.(?:js|css)$/;
function localPath(value,extension){
  if(typeof value!=='string')return null;
  const path=value.replace(/^(?:\.\/|\/)/,'');
  return pathPattern.test(path)&&path.endsWith(extension)?path:null;
}
function cssSubset(text){
  // For this narrow pilot only: no external/imported resources or CSS
  // escapes that would obscure resource references. Browser isolation,
  // not this textual check, remains the actual security boundary.
  return !/\\|@import\b|url\s*\(/i.test(text);
}
function scanHtml(text,manifest){
  let i=0,doctype=false,roots=0,head=0,body=0;
  const stack=[],ids=new Set(),linked=new Set();
  const reject=()=>null;
  while(i<text.length){
    if(text.startsWith('<!--',i)){
      const end=text.indexOf('-->',i+4);
      if(end<0||text.slice(i+4,end).includes('--'))return reject();
      i=end+3;continue;
    }
    if(text[i]!=='<'){
      const end=text.indexOf('<',i),chunk=text.slice(i,end<0?text.length:end);
      if(!stack.length&&chunk.trim())return reject();
      i=end<0?text.length:end;continue;
    }
    const rest=text.slice(i);
    const declaration=/^<!doctype html\s*>/i.exec(rest);
    if(declaration){
      if(doctype||roots||stack.length)return reject();
      doctype=true;i+=declaration[0].length;continue;
    }
    const close=/^<\/([a-z][a-z0-9]*)\s*>/i.exec(rest);
    if(close){
      if(stack.pop()!==close[1].toLowerCase())return reject();
      i+=close[0].length;continue;
    }
    const start=/^<([a-z][a-z0-9]*)(?=[\s>])/i.exec(rest);
    if(!start)return reject();
    const tag=start[1].toLowerCase(),parent=stack.at(-1);
    if(!tags.has(tag)||(!parent&&tag!=='html')||(tag==='html'&&parent)||
      (tag==='head'||tag==='body')&&parent!=='html'||
      parent==='html'&&!['head','body'].includes(tag))return reject();
    if(tag==='html'&&++roots!==1||tag==='head'&&(++head!==1||body!==0)||
      tag==='body'&&(++body!==1||head!==1))return reject();
    i+=start[0].length;
    const attrs=Object.create(null);
    while(text[i]!=='>'){
      // Require whitespace-separated attributes, quoted values and
      // explicitly supported Boolean attributes. No self-closing syntax.
      const attr=/^\s+([a-z][a-z0-9_-]*)(?:\s*=\s*(?:"([^"<>]*)"|'([^'<>]*)'))?/i.exec(text.slice(i));
      if(!attr){
        const end=/^\s*>/.exec(text.slice(i));if(!end)return reject();
        i+=end[0].length-1;break;
      }
      const name=attr[1].toLowerCase(),value=attr[2]??attr[3]??null;
      if(Object.hasOwn(attrs,name)||name.startsWith('on')||
        !(common.has(name)||extra[tag]?.has(name)||/^(?:aria|data)-[a-z0-9_-]+$/.test(name))||
        value===null&&!bools.has(name)||value!==null&&/[\u0000-\u001f\u007f]/.test(value))return reject();
      attrs[name]=value;i+=attr[0].length;
    }
    if(text[i]!=='>'||i>=text.length)return reject();
    i++;
    if(Object.hasOwn(attrs,'id')){
      if(typeof attrs.id!=='string'||!/^[A-Za-z][A-Za-z0-9_-]*$/.test(attrs.id)||ids.has(attrs.id))return reject();
      ids.add(attrs.id);
    }
    if(tag==='meta'&&!((attrs.charset?.toLowerCase()==='utf-8'&&!Object.hasOwn(attrs,'name'))||
      (attrs.name?.toLowerCase()==='viewport'&&typeof attrs.content==='string'&&!Object.hasOwn(attrs,'charset'))))return reject();
    if(tag==='script'||tag==='link'){
      const isScript=tag==='script',target=localPath(isScript?attrs.src:attrs.href,isScript?'.js':'.css');
      if(!target||!manifest.has(target)||linked.has(target)||
        (isScript?Object.hasOwn(attrs,'type')&&attrs.type!=='text/javascript':attrs.rel!=='stylesheet'))return reject();
      linked.add(target);
    }
    if(tag==='script'||tag==='style'||tag==='title'){
      // HTML raw-text elements: do not scan their contents as markup.
      const end=new RegExp('</'+tag+'\\s*>','i').exec(text.slice(i));
      if(!end)return reject();
      const raw=text.slice(i,i+end.index);
      if(tag==='script'&&raw.trim()||tag==='style'&&!cssSubset(raw)||tag==='title'&&raw.includes('<'))return reject();
      i+=end.index+end[0].length;continue;
    }
    if(!voids.has(tag)){
      if(stack.length>=40)return reject();
      stack.push(tag);
    }
  }
  if(!doctype||roots!==1||head!==1||body!==1||stack.length||
    ![...linked].some(p=>p.endsWith('.js')))return reject();
  return linked;
}
export function inspectCandidateBuild(assets,files,checkClassicSyntax){
  if(typeof checkClassicSyntax!=='function'||!Array.isArray(files)||files.length<1||files.length>12)
    throw Error('CANDIDATE_BUILD_INPUT_INVALID');
  let javascript_syntax=true,local_assets_linked=true;
  const manifest=new Map();
  for(const file of files){
    const response=assets.resolve('GET','/'+file.path);
    if(response.status!==200||typeof response.body!=='string')throw Error('CANDIDATE_BUILD_ASSET_MISSING');
    manifest.set(file.path,response.body);
  }
  if([...manifest.keys()].filter(p=>p.endsWith('.html')).length!==1||!manifest.has('index.html'))
    local_assets_linked=false;
  const linked=local_assets_linked?scanHtml(manifest.get('index.html'),manifest):null;
  if(!linked)local_assets_linked=false;
  for(const [name,content] of manifest){
    if(name.endsWith('.js')){
      const syntax=checkClassicSyntax(content);
      if(typeof syntax!=='boolean')throw Error('CANDIDATE_SYNTAX_PROBE_UNAVAILABLE');
      if(!syntax)javascript_syntax=false;
      if(!linked?.has(name))local_assets_linked=false;
    }else if(name.endsWith('.css')){
      if(!linked?.has(name)||!cssSubset(content))local_assets_linked=false;
    }else if(name.endsWith('.json')){
      try{JSON.parse(content);}catch{local_assets_linked=false;}
    }else if(name!=='index.html')local_assets_linked=false;
  }
  return {javascript_syntax,local_assets_linked};
}
