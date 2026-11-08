/* The queue contains drafts only. The existing apply flow owns the single write request. */
(() => {
  const el=id=>document.getElementById(id), singleRequest=window.cylinderApplyRequest, singlePreview=el('cyGenerate')?.onclick;
  if(!el('cyBatchPanel') || typeof singleRequest!=='function')return;
  const copy=value=>JSON.parse(JSON.stringify(value));
  const stable=value=>JSON.stringify(value, function(key,item){
    return item && typeof item==='object' && !Array.isArray(item)
      ? Object.keys(item).sort().reduce((out,k)=>(out[k]=item[k],out),{}) : item;
  });
  const busy=()=>!!(window.cylinderApplying || window.cylinderRecoveryPending);
  let rows=[], common=null, origin='', invalid=false, notice='', previewBusy=false;
  function context() {
    const c=window.cylinderBatchContext?.(), s=window.cylinderIoState?.();
    if(!c || !c.template_confirmed || !c.id || !c.target?.target_token || (s && (s.id!==c.id || stable(s.target)!==stable(c.target))))return null;
    return c;
  }
  const key=c=>stable({id:c.id,target:c.target,host:c.host,sample_id:c.sample_id,sample_network:c.sample_network,source_hash:c.source_hash,rules:c.rules});
  function current() {
    const c=context();
    if(rows.length && (!c || key(c)!==origin))invalid=true;
    return c;
  }
  const address=p=>window.CylinderModel?.pointAddress(p.address) || String(p.address||'').trim().replace(/^%/,'').toUpperCase();
  const folded=s=>String(s||'').trim().toLocaleLowerCase();
  function sync() {
    current();
    const active=busy();
    el('cyBatchAdd').disabled=active || invalid || rows.length>=20;
    el('cyBatchClear').disabled=active || !rows.length;
    el('cyBatchRows').querySelectorAll('button').forEach(button=>button.disabled=active);
    if(el('cyGenerate')){el('cyGenerate').textContent=rows.length?'预览批量生成结果':'预览生成结果';el('cyGenerate').disabled=active || previewBusy;}
    ['cyDownload','cyDownloadPlan'].forEach(id=>{if(el(id)){el(id).hidden=!!rows.length;el(id).style.display=rows.length?'none':'';}});
    if(invalid && el('cyResult'))el('cyResult').hidden=true;
    el('cyBatchStatus').textContent=invalid
      ? '工程、读取快照或模板已变化，原批量清单已失效。请清空清单后重新加入，尚未写入。'
      : notice || (rows.length ? '批量清单共 '+rows.length+' 个气缸。生成按钮只提交清单中的项目；正在填写的新一行需要先加入。' : '填写一个气缸后加入清单，可连续添加，最多 20 个；也可直接生成单个气缸。');
    if(!active)['cyApply','cyApplyPreview'].forEach(id=>{if(el(id))el(id).textContent=rows.length?'批量生成并写入 '+rows.length+' 个气缸':'生成并写入博途';});
  }
  function render() {
    if(el('cyResult'))el('cyResult').hidden=true;
    const container=el('cyBatchRows');container.replaceChildren();
    if(rows.length) {
      const table=document.createElement('table');table.className='execution-table';
      const header=table.createTHead().insertRow();
      ['气缸名称','程序实例 / 索引','输入与输出点位','操作'].forEach(text=>{const th=document.createElement('th');th.textContent=text;header.appendChild(th);});
      rows.forEach((entry,index)=>{
        const tr=table.insertRow();tr.insertCell().textContent=entry.request.cylinder_name;
        tr.insertCell().textContent=entry.request.instance_name+' / '+(entry.request.alarm_index||'—');
        const points=tr.insertCell();points.style.whiteSpace='pre-line';
        points.textContent=entry.points.map(p=>p.pin+'：'+p.address+' · '+p.name).join('\n');
        const button=document.createElement('button');button.type='button';button.className='btn btn-sm';button.textContent='移除';
        button.onclick=()=>{if(busy())return;rows.splice(index,1);notice='';if(el('cyResult'))el('cyResult').hidden=true;if(!rows.length){common=null;origin='';invalid=false;}render();};
        tr.insertCell().appendChild(button);
      });container.appendChild(table);
    }
    sync();
  }
  function add() {
    if(busy())return;
    const c=current();
    if(invalid){sync();return;}
    if(!c){notice='请先读取工程并确认项目模板，再加入批量清单。';sync();return;}
    if(rows.length>=20){notice='最多加入 20 个气缸，请先提交本批次。';sync();return;}
    const request=singleRequest();if(!request)return;
    try {
      const entry={cylinder_name:request.cylinder_name.trim(),instance_name:request.instance_name.trim(),alarm_index:String(request.alarm_index||'').trim(),bindings:copy(request.bindings)};
      if(!entry.cylinder_name || !entry.instance_name)throw new Error('请填写气缸名称和程序实例名。');
      const indexKey=value=>/^\d+$/.test(value)?String(Number(value)):value;
      if(rows.some(r=>folded(r.request.cylinder_name)===folded(entry.cylinder_name)))throw new Error('清单中已有相同气缸名称。');
      if(rows.some(r=>folded(r.request.instance_name)===folded(entry.instance_name)))throw new Error('清单中已有相同程序实例名。');
      if(entry.alarm_index && rows.some(r=>indexKey(r.request.alarm_index)===indexKey(entry.alarm_index)))throw new Error('清单中已有相同数组索引。');
      const reserved=new Set(rows.flatMap(r=>r.points.map(p=>p.address))), selected=new Set(), points=[];
      for(const [pin,role] of Object.entries(c.rules||{})) {
        if(role!=='input' && role!=='output')continue;
        const point=c.points.find(p=>p.id===entry.bindings[pin]), physical=point && address(point);
        if(!point || !/^[IQ]\d+\.[0-7]$/.test(physical||'') || physical[0]!== (role==='input'?'I':'Q'))throw new Error(pin+' 尚未选择有效点位。');
        if(reserved.has(physical) || selected.has(physical))throw new Error(physical+' 已被本批次气缸使用，请选择其他点位。');
        selected.add(physical);points.push({pin,address:physical,name:point.symbol||point.comment||point.id});
      }
      if(!rows.length) {
        common=copy(request);['cylinder_name','instance_name','alarm_index','bindings'].forEach(k=>delete common[k]);origin=key(c);
      }
      rows.push({request:entry,points});notice='';
      ['cyDisplayName','cyName','cyAlarm'].forEach(id=>{if(el(id))el(id).value='';});
      const controls=[...el('cyPoints').querySelectorAll('select[data-pin],input')];controls.forEach(control=>control.value='');
      controls.forEach(control=>control.dispatchEvent(new Event(control.tagName==='SELECT'?'change':'input',{bubbles:true})));
      if(el('cyDisplayName'))el('cyDisplayName').dispatchEvent(new Event('input',{bubbles:true}));
      render();
    } catch(error) {notice='未加入：'+error.message;sync();}
  }
  el('cyBatchAdd').onclick=add;
  el('cyBatchClear').onclick=()=>{if(busy())return;rows=[];common=null;origin='';invalid=false;notice='清单已清空，尚未写入。';if(el('cyResult'))el('cyResult').hidden=true;render();};
  window.cylinderApplyRequest=()=>{
    if(busy())return null;
    if(!rows.length)return singleRequest();
    current();if(invalid){sync();return null;}
    return {...copy(common),cylinder_name:'批量 '+rows.length+' 个气缸',instance_name:rows.map(r=>r.request.instance_name).join('、'),batch:rows.map(r=>copy(r.request))};
  };
  if(el('cyGenerate'))el('cyGenerate').onclick=async()=>{
    if(busy() || previewBusy)return;
    if(!rows.length)return singlePreview?.();
    const request=window.cylinderApplyRequest();if(!request)return;
    const ticket=stable(request);previewBusy=true;notice='正在检查批量方案，尚未写入…';sync();
    try {
      const response=await fetch('/api/io/cylinder-batch-preview',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)});
      const result=await response.json();current();
      if(busy() || invalid || !rows.length || stable(window.cylinderApplyRequest())!==ticket)return;
      if(!response.ok)throw new Error(result.error||'批量方案检查失败');
      const summary=result.summary||{}, table=document.createElement('table');table.className='execution-table';
      const header=table.createTHead().insertRow();['气缸 / 实例','参数','地址','生成的点位名称'].forEach(value=>{const th=document.createElement('th');th.textContent=value;header.appendChild(th);});
      for(const cylinder of summary.cylinders||[])for(const rename of cylinder.renames||[]){
        const row=table.insertRow();[cylinder.cylinder_name+' / '+cylinder.instance_name,rename.pin,rename.address,rename.new_symbol].forEach(value=>row.insertCell().textContent=value||'');
      }
      el('cyDiff').replaceChildren(table);el('cyCode').textContent='';
      el('cyWarnings').textContent='批量方案检查通过：'+rows.length+' 个气缸，将更新 '+summary.blocks+' 个程序块。\n'+(summary.note||'');el('cyResult').hidden=false;
      notice='批量方案检查通过，尚未写入。请核对下面的全部点位，再点击批量生成并写入。';
    } catch(error) {current();if(!invalid && rows.length && stable(window.cylinderApplyRequest())===ticket)notice='批量方案未通过：'+error.message;}
    finally {previewBusy=false;if(notice==='正在检查批量方案，尚未写入…')notice='清单已变化，请重新预览。';sync();}
  };
  window.cylinderBatchSync=sync;
  const originalSync=window.cylinderSync;
  if(originalSync)window.cylinderSync=(...args)=>{const result=originalSync(...args);sync();return result;};
  document.addEventListener('change',()=>queueMicrotask(sync));
  window.addEventListener('cylinder-recovery-pending',sync);
  window.addEventListener('cylinder-job-finished',()=>{if(rows.length){invalid=true;notice='';sync();}});
  // This observes only local draft context, with no requests or write retries.
  setInterval(()=>{if(rows.length)sync();},1000);
  render();
})();
