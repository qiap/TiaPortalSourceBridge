/* Read-only electrical workbook comparison; all server results are target-bound. */
(() => {
  const el=id=>document.getElementById(id);if(!el('recRun'))return;
  const statuses={match:'所选字段一致',different:'字段差异',excel_only:'仅 Excel 列出',plc_only:'仅 PLC 列出',conflict:'地址或名称冲突',ambiguous:'存在多个匹配',invalid:'Excel 数据待修正'};
  const fields={address:'recAddress',name:'recName',type:'recType',comment:'recComment'};
  const state=()=>{const s=window.cylinderIoState?.()||{id:'',busy:false,target:{}};return {...s,target:{...s.target}};};
  const targetKey=t=>JSON.stringify([(t?.project_path||'').replace(/\\/g,'/').toLowerCase(),t?.plc_name||'']);
  const key=s=>targetKey(s.target)+'|'+s.id;
  const node=(tag,value)=>{const e=document.createElement(tag);e.textContent=value??'';return e;};
  const message=value=>el('recStatus').textContent=value;
  let upload=null,preview=null,choices=[],report=null,reportKey='',uploadTarget='',lastTarget='',lastSnapshot='',sequence=0,revision=0,busy=false,exporting=false,page=1;
  async function json(url,options){const response=await fetch(url,{cache:'no-store',...options});const data=await response.json();if(!response.ok)throw Error(data.error||'请求失败');return data;}
  const post=(path,data)=>json('/api/io/reconcile-'+path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
  function clearReport(){report=null;reportKey='';el('recReport').hidden=true;el('recRows').replaceChildren();revision++;}
  function changed(){clearReport();message('列或比较范围已变化，请重新对账。');sync();}
  function sync(){
    const s=state(),t=targetKey(s.target);
    el('recTarget').textContent=[s.target.project_path||'未选择工程',s.target.plc_name||'未选择 PLC'].join(' · ');
    if(lastTarget&&lastTarget!==t){sequence++;upload=null;preview=null;choices=[];el('recMapping').hidden=true;el('recFile').value='';clearReport();message('工程或 PLC 已切换，请读取点表并重新上传 Excel。');}
    lastTarget=t;
    if(lastSnapshot!==s.id){
      if(report){clearReport();message('PLC 点表快照已变化，请重新对账。');}
      choices=[];lastSnapshot=s.id;renderScopes();
      if(s.id)loadChoices(s);
    }
    const blocked=busy||exporting||s.busy||!!window.cylinderApplying||!!window.cylinderRecoveryPending;
    el('recCached').disabled=el('recLive').disabled=blocked||!s.target.target_token||!s.target.plc_name;
    el('recUpload').disabled=blocked||!el('recFile').files?.length||!s.target.target_token;
    for(const id of ['recFile','recSheet','recHeader','recPreview','recAddress','recName','recType','recComment','recScope','recUnit','recTable'])el(id).disabled=blocked;
    el('recRun').disabled=blocked||!upload||!preview||!s.id||!selectedUnit()||el('recAddress').value===''||Number(el('recAddress').value)<0||el('recName').value===''||Number(el('recName').value)<0;
    el('recExport').disabled=blocked||!report||reportKey!==key(s);
  }
  async function loadChoices(selected){
    try{const data=await json('/api/io/reconcile-options?'+new URLSearchParams({id:selected.id}));if(key(selected)!==key(state()))return;
      if(data.snapshot_id!==selected.id||targetKey(data)!==targetKey(selected.target))throw Error('PLC 点表范围不属于当前目标。');
      choices=data.choices.scopes;renderScopes();sync();
    }catch(error){if(key(selected)===key(state()))message('无法读取 PLC 对账范围：'+error.message);}
  }
  function selectedScope(){return choices.find(c=>c.scope===el('recScope').value);}
  function selectedUnit(){return selectedScope()?.units.find(u=>u.name===el('recUnit').value);}
  function renderScopes(){
    const units=selectedScope()?.units||[],old=el('recUnit').value;
    el('recUnit').replaceChildren(...units.map(u=>new Option((u.name||'默认软件单元')+' · '+u.count+' 项',u.name)));
    if(units.some(u=>u.name===old))el('recUnit').value=old;
    renderTables();
  }
  function renderTables(){el('recTable').replaceChildren(new Option('全部变量表',''),...(selectedUnit()?.tables||[]).map(t=>new Option(t.name+' · '+t.count+' 项',t.key)));}
  function showPreview(data){
    preview=data;el('recSheet').value=data.sheet;el('recHeader').value=data.header_row;
    for(const [field,id] of Object.entries(fields)){
      el(id).replaceChildren(new Option(field==='address'||field==='name'?'请选择列':'不比较此列','-1'),...data.columns.map(c=>new Option(c.letter+' · '+(c.error?'['+c.error+']':c.title||'空标题'),String(c.index))));
      el(id).value=String(data.suggested[field]??-1);
    }
    const table=el('recSample');table.replaceChildren();const head=table.createTHead().insertRow();head.append(node('th','Excel 行'));
    data.columns.slice(0,12).forEach(c=>head.append(node('th',c.letter+' · '+c.title)));
    const body=table.createTBody();for(const sample of data.samples){const row=body.insertRow();row.append(node('td',sample.row));sample.values.slice(0,12).forEach(v=>row.append(node('td',v)));}
    el('recMapping').hidden=false;clearReport();
  }
  async function read(mode){
    sync();if(el(mode==='source'?'recCached':'recLive').disabled)return;
    const before=state().id;busy=true;clearReport();sync();message('正在读取 PLC 点表…');
    try{await window.ioRead(mode);if(!state().id||state().id===before)throw Error('未取得新的 PLC 点表，请查看上方读取信息。');message('PLC 点表已就绪，请上传 Excel 或开始对账。');}
    catch(error){message('读取失败：'+error.message);}finally{busy=false;sync();}
  }
  async function uploadFile(){
    sync();if(el('recUpload').disabled)return;const file=el('recFile').files[0],selected=state(),seq=++sequence;
    clearReport();upload=null;preview=null;el('recMapping').hidden=true;
    if(!/\.xlsx$/i.test(file.name)||file.size>8*1024*1024||file.size===0){message('请选择不超过 8 MB 的 .xlsx 文件。');sync();return;}
    busy=true;sync();message('正在读取 Excel…');
    try{
      const base64=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result).split(',')[1]);reader.onerror=()=>reject(Error('文件读取失败'));reader.readAsDataURL(file);});
      if(seq!==sequence||targetKey(selected.target)!==targetKey(state().target))return;
      const data=await post('upload',{...selected.target,file_name:file.name,content_base64:base64});
      if(seq!==sequence||targetKey(selected.target)!==targetKey(state().target))return;
      if(targetKey(data)!==targetKey(selected.target))throw Error('上传结果目标不匹配。');
      upload=data;uploadTarget=targetKey(selected.target);el('recFileName').textContent=data.file_name;
      el('recSheet').replaceChildren(...data.sheets.map(s=>new Option(s.name+(s.hidden?'（隐藏）':'')+' · '+s.rows+' 行',s.name)));
      showPreview(data.preview);message('Excel 已读取。请核对表头、列对应关系和 PLC 范围，再开始对账。');
    }catch(error){if(seq===sequence)message('读取 Excel 失败：'+error.message);}finally{busy=false;sync();}
  }
  async function updatePreview(automatic){
    if(busy||!upload)return;const seq=++sequence,selected=upload.upload_id,sheet=el('recSheet').value;
    const header=automatic?0:Number(el('recHeader').value);clearReport();preview=null;busy=true;sync();message('正在更新工作表预览…');
    try{
      const data=await json('/api/io/reconcile-preview?'+new URLSearchParams({upload_id:selected,sheet,header_row:header}));
      if(seq!==sequence||!upload||selected!==upload.upload_id||uploadTarget!==targetKey(state().target))return;
      showPreview(data);message('预览已更新，请重新核对列对应关系。');
    }catch(error){if(seq===sequence)message('预览失败：'+error.message);}finally{busy=false;sync();}
  }
  async function run(){
    sync();if(el('recRun').disabled)return;
    if(Number(el('recHeader').value)!==preview.header_row||el('recSheet').value!==preview.sheet){message('表头已改变，请先点击“更新表头预览”。');return;}
    const selected=state(),seq=++sequence;clearReport();const rev=revision;
    const options={sheet:preview.sheet,header_row:preview.header_row,scope:el('recScope').value,unit:el('recUnit').value,table:el('recTable').value};
    for(const [field,id] of Object.entries(fields))options[field]=Number(el(id).value);
    busy=true;sync();message('正在对比 Excel 与 PLC 快照…');
    try{
      const data=await post('run',{...selected.target,id:selected.id,upload_id:upload.upload_id,options});
      if(seq!==sequence||rev!==revision||key(selected)!==key(state())){message('工程、快照或列选择已变化，已丢弃旧对账结果。');return;}
      if(data.snapshot_id!==selected.id||targetKey(data)!==targetKey(selected.target))throw Error('对账报告目标不匹配。');
      report=data;reportKey=key(selected);page=1;el('recFilter').value='differences';el('recSearch').value='';renderReport();message('只读对账完成。请核对差异及候选证据，本次未修改工程。');
    }catch(error){if(seq===sequence)message('对账失败：'+error.message);}finally{busy=false;sync();}
  }
  function renderReport(){
    el('recReport').hidden=false;const r=report,o=r.options;
    el('recReportSource').textContent=[r.project_path+' · '+r.plc_name,r.file_name+' / '+o.sheet+' · 表头第 '+o.header_row+' 行',
      (o.scope==='mapped'?'映射后的 I/O 点位':'原始变量表')+' · '+(o.unit||'默认软件单元')+' · '+(o.table?.replace(/\n/g,' / ')||'全部变量表'),
      (r.source_mode==='local_cache'?'本地缓存源码':'从博途读取')+' · 快照 '+r.snapshot_started+' 至 '+r.snapshot_completed,r.source_note||''].filter(Boolean).join('\n');
    el('recSummary').textContent='Excel '+r.excel_rows+' 行 / PLC '+r.plc_rows+' 项 · '+Object.entries(r.counts).map(([k,v])=>(statuses[k]||k)+' '+v).join(' · ');
    el('recWarnings').replaceChildren(...r.warnings.map(w=>node('li',w)));renderRows();
  }
  function renderRows(){
    if(!report)return;const filter=el('recFilter').value,q=el('recSearch').value.trim().toLocaleLowerCase();
    const rows=report.rows.filter(r=>(filter==='all'||filter==='differences'&&r.status!=='match'||filter===r.status)&&(!q||JSON.stringify(r).toLocaleLowerCase().includes(q)));
    const pages=Math.max(1,Math.ceil(rows.length/50));page=Math.max(1,Math.min(page,pages));
    el('recPage').textContent='筛选 '+rows.length+' / 全部 '+report.rows.length+' 项 · 第 '+page+' / '+pages+' 页';el('recPrev').disabled=page<=1;el('recNext').disabled=page>=pages;
    el('recRows').replaceChildren(...rows.slice((page-1)*50,page*50).map(r=>{
      const tr=document.createElement('tr');const values=[r.excel_row||'—',(statuses[r.status]||r.status)+'\n'+r.issues.join('\n'),r.address||'—',r.plc.map(p=>p.address).join('\n')||'—',
        [r.name,r.type,r.comment].filter(Boolean).join('\n')||'—',r.plc.map(p=>[p.name,p.type,p.comment].filter(Boolean).join('\n')).join('\n────\n')||'—',
        r.plc.map(p=>[p.unit,p.group,p.table,p.mapping,'引用证据 '+p.references+' 条'].filter(Boolean).join(' / ')).join('\n')||'—'];
      for(const value of values){const td=node('td',value);td.style.whiteSpace='pre-wrap';td.style.overflowWrap='anywhere';tr.append(td);}return tr;
    }));
    if(!rows.length){const tr=document.createElement('tr'),td=node('td','当前筛选没有结果；请查看全部结果与覆盖说明。');td.colSpan=7;tr.append(td);el('recRows').append(tr);}
  }
  async function exportReport(){
    sync();if(el('recExport').disabled)return;const id=report.id,context=reportKey,rev=revision;exporting=true;sync();
    try{const response=await fetch('/api/io/reconcile-export?'+new URLSearchParams({id}),{cache:'no-store'});if(!response.ok){const e=await response.json();throw Error(e.error||'导出失败');}
      const blob=await response.blob();if(!report||report.id!==id||context!==key(state())||rev!==revision)throw Error('导出期间目标、快照或列选择已变化，请重新对账。');
      const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='TIA-电气点表对账-'+new Date().toISOString().slice(0,10)+'.xlsx';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);message('已导出全部对账结果和 PLC 候选证据，不受页面筛选影响。');
    }catch(error){message('导出失败：'+error.message);}finally{exporting=false;sync();}
  }
  el('recCached').onclick=()=>read('source');el('recLive').onclick=()=>read('live');el('recUpload').onclick=uploadFile;
  el('recFile').onchange=()=>{upload=null;preview=null;sequence++;clearReport();el('recMapping').hidden=true;sync();};
  el('recSheet').onchange=()=>updatePreview(true);el('recHeader').oninput=changed;el('recPreview').onclick=()=>updatePreview(false);
  for(const id of Object.values(fields))el(id).onchange=changed;
  el('recScope').onchange=()=>{renderScopes();changed();};el('recUnit').onchange=()=>{renderTables();changed();};el('recTable').onchange=changed;
  el('recRun').onclick=run;el('recExport').onclick=exportReport;
  el('recFilter').onchange=el('recSearch').oninput=()=>{page=1;renderRows();};el('recPrev').onclick=()=>{page--;renderRows();};el('recNext').onclick=()=>{page++;renderRows();};
  window.ioReconcileSync=sync;sync();setInterval(sync,750);
})();
