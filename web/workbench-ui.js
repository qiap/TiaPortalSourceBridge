(() => {
  'use strict';
  const B=window.BridgeUI,n=B.node,$=id=>document.getElementById(id);
  const button=(id,title,click,primary=false)=>{const e=n('button',title,'btn'+(primary?' btn-primary':''));e.type='button';e.id=id;e.onclick=click;return e;};
  function field(id,label,type='text',options){const wrap=n('label',label),el=n(options?'select':'input');el.id=id;el.setAttribute('aria-label',label);if(options)options.forEach(([value,text])=>el.append(new Option(text,value)));else el.type=type;wrap.append(el);return wrap;}
  function section(id,title,parent){const s=n('section',null,'card workbench-pane');s.id=id;s.append(n('h3',title));parent.append(s);return s;}
  function table(headers,rows){const wrap=n('div',null,'task-table-wrap'),t=n('table'),head=t.createTHead().insertRow();headers.forEach(h=>head.append(n('th',h)));const body=t.createTBody();for(const values of rows){const tr=body.insertRow();for(const value of values){const td=n('td');if(value instanceof Node)td.append(value);else td.textContent=value==null?'—':String(value);tr.append(td);}}wrap.append(t);return wrap;}
  function details(title,value){const d=n('details',null,'advanced-editor');d.append(n('summary',title),n('pre',typeof value==='string'?value:JSON.stringify(value,null,2)));return d;}
  function evidence(data){const box=n('div',null,'source-badge');const source=data.source_kind||data.source||'';const cached=source==='index_cache'||data.freshness==='known_stale'||data.cache_state==='catalog_only';box.append(n('strong',cached?'本地源码快照 · 可能落后于博途':source==='live_export'?'实时工程读取':'数据来源待核对'));box.append(n('div',[source,data.indexed_utc?'采集：'+data.indexed_utc:'',data.catalog_utc?'目录：'+data.catalog_utc:'',data.read_utc?'读取：'+data.read_utc:'',data.cache_state,data.freshness].filter(Boolean).join(' · ')));if(data.cache_state==='catalog_only'||data.code==='NOT_INDEXED'||data.code==='CACHE_SOURCE_UNAVAILABLE')box.append(n('p','未缓存源码，不代表工程中已删除。','evidence-warning'));return box;}
  async function post(action,data={},binding=B.target(),base='/api/io/workbench/'){const response=await fetch(base+action,{method:'POST',headers:{'Content-Type':'application/json'},cache:'no-store',body:JSON.stringify({...binding,...data})});const result=await response.json();if(!response.ok)throw Error(result.error||'请求失败');return result;}
  const key=()=>JSON.stringify(B.target());
  function bind(panel,clear){let scope=key();B.watch(panel.id,()=>{const next=key();if(scope!==next){scope=next;clear();}});}
  function result(box,record){B.result(box.id,record.result?{...record.result,state:record.state,id:record.id,saved:record.result.saved??record.saved,automatic_retry:record.automatic_retry??record.result.automatic_retry}:record);}
  async function poll(id,binding,accept){for(;;){const r=await post('job',{id},binding);if(!accept(r))return;if(!['accepted','running'].includes(r.state))return;await new Promise(resolve=>setTimeout(resolve,800));}}
  window.WorkbenchUI={n,$,button,field,section,table,details,evidence,post,key,bind,result,poll};
})();
