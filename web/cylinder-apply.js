/* A lost connection resumes status polling; it never resubmits an engineering write. */
(() => {
  const el=id=>document.getElementById(id), storageKey='tia-cylinder-pending-write-v1', lastKey='tia-cylinder-last-operation-v1';
  let pending=null, polling=false, locked=[], uiBusy=false;
  const show=text=>{el('cyApplyPanel').hidden=false;el('cyApplyStatus').textContent=text;};
  function remember() {
    try { if(pending)localStorage.setItem(storageKey,JSON.stringify(pending));else localStorage.removeItem(storageKey); } catch(_) {}
  }
  function busy(active) {
    if(uiBusy===active)return;
    uiBusy=active;
    window.cylinderApplying=active;
    if(active) {
      locked=[...document.querySelectorAll('#cyEditor input,#cyEditor select,#cyEditor button,#cyCached,#cyLive')].map(e=>[e,e.disabled]);
      locked.forEach(([e])=>e.disabled=true);
    } else {
      locked.forEach(([e,was])=>e.disabled=was);locked=[];
    }
    ['cyApply','cyApplyPreview'].forEach(id=>el(id).textContent=active?'正在生成并写入…':'生成并写入博途');
    if(window.cylinderBatchSync)window.cylinderBatchSync();
  }
  function historyLink(job, container) {
    if(!job.id || !window.openEngineeringTask)return;
    const link=document.createElement('button');link.type='button';link.className='btn btn-sm';link.textContent='查看完整记录与后续处理';
    link.onclick=()=>window.openEngineeringTask(job.id);container.appendChild(link);
  }
  function resultView(job) {
    const container=el('cyApplySummary');container.replaceChildren();
    const result=job.result || {};
    el('cyApplyDetails').hidden=!job.result;
    el('cyApplyReport').textContent=job.result?JSON.stringify(job.result,null,2):'';
    const batch=result.import || result.import_result || result.batch;
    const items=batch?.items || [];
    if(batch?.error_count!=null || batch?.warning_count!=null) {
      const p=document.createElement('p');p.textContent='编译：'+(batch.error_count??'未确认')+' 个错误，'+(batch.warning_count??'未确认')+' 条警告；'+(result.saved===true?'工程已保存':'保存未确认');container.appendChild(p);
    }
    if(items.length) {
      const table=document.createElement('table');table.className='execution-table';
      const header=table.createTHead().insertRow();
      ['程序块','写入与检查','备份位置'].forEach(s=>{const th=document.createElement('th');th.textContent=s;header.appendChild(th);});
      items.forEach(item=>{
        const row=table.insertRow();
        const status={verified:'回读一致',imported:'已导入',completed:'完成',failed:'失败',backed_up:'已备份'};
        [item.block_name,[status[item.status]||item.status,item.verification,item.error].filter(Boolean).join(' · '),item.backup||''].forEach(v=>{
          const cell=row.insertCell();cell.textContent=typeof v==='object'?JSON.stringify(v):v;cell.style.overflowWrap='anywhere';
        });
      });container.appendChild(table);
    }
    historyLink(job,container);
  }
  function finish(job, invalidate=true) {
    const titles={completed:'生成并写入成功',failed:'本次任务失败',needs_review:'需要核对工程中的执行结果',outcome_unknown:'暂时无法确认写入结果'};
    show((titles[job.state]||'任务结束')+'\n'+[job.message,job.error].filter((s,i,a)=>s&&a.indexOf(s)===i).join('\n'));
    if(!job.id && pending?.id)job.id=pending.id;
    if(job.id)try{localStorage.setItem(lastKey,job.id);}catch(_){}
    resultView(job);pending=null;remember();busy(!!window.cylinderRecoveryPending);
    window.dispatchEvent(new CustomEvent('cylinder-job-finished',{detail:{id:job.id}}));
    if(invalidate && window.cylinderIoInvalidate)window.cylinderIoInvalidate();
    else if(window.cylinderSync)window.cylinderSync();
  }
  async function poll() {
    if(polling || !pending)return;polling=true;
    let missingSince=0;
    try {
      while(pending) {
        try {
          const query=pending.id?{id:pending.id}:{request_id:pending.request_id};
          const response=await fetch('/api/io/cylinder-apply-status?'+new URLSearchParams(query),{cache:'no-store'});
          if(response.status===404) {
            if(!missingSince)missingSince=Date.now();
            if(Date.now()-missingSince>30000) {
              show('尚未找到这次任务的结果，仍在查询原操作编号。请保留现场并核对后台状态；不会因等待超时而再次写入。');
              await new Promise(resolve=>setTimeout(resolve,5000));continue;
            }
            throw new Error('正在查找已提交的写入任务');
          }
          if(!response.ok)throw new Error('暂时无法读取任务状态');
          const job=await response.json();missingSince=0;
          if(job.id&&!pending.id){pending.id=job.id;remember();}
          if(['completed','failed','needs_review','outcome_unknown'].includes(job.state)){finish(job);break;}
          show(job.message||'正在执行，请稍候…');
        } catch(e) {show('暂时未能读取进度，正在恢复连接。已提交的写入不会重复发送。\n'+e.message);}
        await new Promise(resolve=>setTimeout(resolve,1500));
      }
    } finally {polling=false;}
  }
  async function apply() {
    if(window.cylinderApplying || window.cylinderRecoveryPending)return;
    const request=window.cylinderApplyRequest();if(!request)return;
    request.request_id=crypto.randomUUID();
    pending={request_id:request.request_id,id:'',target:request.project_path+' · '+request.plc_name,
      name:request.cylinder_name+' / '+request.instance_name,started_at:new Date().toISOString()};
    remember();busy(true);el('cyApplyTarget').textContent=pending.target+' · '+pending.name;
    el('cyApplyDetails').hidden=true;el('cyApplySummary').replaceChildren();
    show('正在提交任务，准备重新读取工程、检查占用并生成气缸…');
    el('cyApplyPanel').scrollIntoView({behavior:'smooth',block:'nearest'});
    try {
      const response=await fetch('/api/io/cylinder-apply',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)});
      const result=await response.json();
      if(!response.ok) {
        if(response.status<500){finish({state:'failed',message:result.error||'写入请求未被接受'},!!result.id);return;}
        throw new Error(result.error||'未收到任务提交结果');
      }
      if(!result.id)throw new Error('尚未收到任务编号');
      pending.id=result.id;remember();
    } catch(e) {show('提交结果尚未确认，正在查询同一次任务，不会重复写入。\n'+e.message);}
    await poll();
  }
  el('cyApply').onclick=el('cyApplyPreview').onclick=apply;
  window.addEventListener('cylinder-recovery-pending',()=>{if(!pending)busy(!!window.cylinderRecoveryPending);});
  try {
    const saved=JSON.parse(localStorage.getItem(storageKey));
    if(saved && typeof saved.request_id==='string' && /^[a-zA-Z0-9-]{16,64}$/.test(saved.request_id))pending=saved;
  }catch(_){}
  if(pending) {
    busy(true);el('cyApplyTarget').textContent=[pending.target,pending.name].filter(Boolean).join(' · ');
    show('正在恢复上次写入任务的进度…');poll();
  } else {
    if(window.cylinderRecoveryPending)busy(true);
    try {
      const id=localStorage.getItem(lastKey);
      if(id && window.openEngineeringTask) {
        const p=document.createElement('p');p.textContent='上次操作的完整记录已保存。';
        el('cyApplyPanel').hidden=false;el('cyApplySummary').appendChild(p);historyLink({id},el('cyApplySummary'));
      }
    }catch(_){}
  }
})();
