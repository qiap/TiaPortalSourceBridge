(() => {
  'use strict';
  const el=id=>document.getElementById(id);if(!el('tagReviewPreview'))return;
  const node=(tag,text)=>{const n=document.createElement(tag);n.textContent=text??'';return n;};
  const state=()=>window.ioReconcileReview?.()||{target:{}};
  const key=()=>JSON.stringify([state().target.project_path,state().target.plc_name]);
  const v17=()=>/\.ap17$/i.test(state().target.project_path||'');
  let selected=new Set(),plan=null,busy=false,generation=0,scope='',reportId='',pollTimer=null;
  const message=text=>el('tagReviewStatus').textContent=text;
  function reset(){selected.clear();plan=null;generation++;el('tagReviewPlan').replaceChildren();el('tagReviewConfirm').checked=false;}
  window.tagReviewReset=reset;
  function sync(){
    if(scope!==key()){scope=key();reset();clearTimeout(pollTimer);el('tagReviewRecords').replaceChildren();message('请选择当前工程的原始变量表进行对账。');}
    const s=state(),id=s.report?.id||'';if(id!==reportId){reportId=id;reset();}
    const blocked=busy||s.busy||!!window.cylinderApplying||!!window.cylinderRecoveryPending;
    el('tagReviewPreview').disabled=blocked||!v17()||!s.valid||!selected.size||s.report?.options.scope!=='native'||!!s.report?.options.unit||!s.report?.options.table;
    el('tagReviewApply').disabled=blocked||!v17()||!plan||!el('tagReviewConfirm').checked;
    el('tagReviewHistory').disabled=busy||!v17()||!s.target.target_token;
    el('tagReviewCulture').disabled=blocked;el('tagReviewConfirm').disabled=blocked||!plan;
    document.querySelectorAll('[data-tag-review-row]').forEach(c=>c.disabled=blocked||!v17());
    document.querySelectorAll('[data-tag-review-restore]').forEach(c=>c.disabled=blocked||!v17());
  }
  window.tagReviewRow=(row,cell)=>{
    sync();
    if(!['different','excel_only'].includes(row.status)||!v17())return;
    const c=document.createElement('input');c.type='checkbox';c.dataset.tagReviewRow=row.excel_row;c.setAttribute('aria-label','选择 Excel 第 '+row.excel_row+' 行');c.checked=selected.has(row.excel_row);
    c.onchange=()=>{if(c.checked)selected.add(row.excel_row);else selected.delete(row.excel_row);plan=null;generation++;el('tagReviewConfirm').checked=false;el('tagReviewPlan').replaceChildren();sync();};cell.prepend(c);
  };
  async function request(action,data={},get=false){
    const payload={...state().target,...data};const response=await fetch('/api/io/tag-review/'+action+(get?'?'+new URLSearchParams(payload):''),get?{cache:'no-store'}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    const result=await response.json();if(!response.ok)throw Error(result.error||'请求失败');return result;
  }
  async function work(label,fn){sync();if(busy)return;busy=true;sync();message(label);const ticket=generation,target=key();try{await fn(()=>ticket===generation&&target===key());}catch(e){if(ticket===generation&&target===key())message('操作失败：'+e.message);}finally{busy=false;sync();}}
  function showPlan(data){
    plan=data;el('tagReviewConfirm').checked=false;const box=el('tagReviewPlan');box.replaceChildren(node('p',(data.recovery_of?'恢复备份 · ':'写回预览 · ')+data.table_name),node('p',data.warning));
    for(const change of (data.diff?.changes||[]).slice(0,100)){const d=node('details','');d.append(node('summary',change.type+' · '+change.path),node('pre','之前\n'+(change.before??'（无）')+'\n之后\n'+(change.after??'（无）')));box.append(d);}
    if(data.diff?.changes?.length>100)box.append(node('p','结构变化超过 100 项，请缩小每批行数后分别核对。'));
    // Every change must be reviewable before a write can be confirmed.
    if(data.diff?.status!=='structural'||data.diff.changes.length>100){plan=null;message('本次预览不能完整显示，请缩小范围。');}
  }
  async function history(valid=()=>true){const rows=await request('history',{},true);if(!valid())return;renderRecords(rows);}
  function renderRecords(rows){
    const box=el('tagReviewRecords');box.replaceChildren();
    const names={completed:'成功',needs_review:'需要核对',failed:'失败（尚未写入）',accepted:'已提交',not_started:'未执行',checking:'检查中',backed_up:'已备份',importing:'正在写入',compiling:'正在编译',verifying:'正在回读',saving:'正在保存'};
    for(const row of rows){const card=node('details','');card.append(node('summary',(names[row.state]||row.state)+' · '+row.table_name+' · '+(row.updated_utc||'')),node('p',row.error||''),node('p','备份：'+(row.backup||'尚无')),node('pre',row.compile_report||''));
      if(row.state==='needs_review'||row.state==='completed'){const restore=node('button','核对现场并预览恢复');restore.className='btn';restore.dataset.tagReviewRestore='true';restore.disabled=busy;
        restore.onclick=()=>work('正在核对当前变量表和备份…',async valid=>{const data=await request('restore-preview',{id:row.id});if(valid()){showPlan(data);message('恢复方案已生成，请核对后再确认写入。');}});card.append(restore);}
      box.append(card);
    }
  }
  el('tagReviewPreview').onclick=()=>work('正在生成点表修改预览…',async valid=>{const data=await request('preview',{report_id:state().report.id,rows:[...selected],culture:el('tagReviewCulture').value});if(valid()){showPlan(data);message('预览已生成，尚未写入。');}});
  el('tagReviewCulture').onchange=()=>{plan=null;generation++;el('tagReviewConfirm').checked=false;el('tagReviewPlan').replaceChildren();sync();};el('tagReviewConfirm').onchange=sync;
  el('tagReviewHistory').onclick=()=>work('正在读取执行记录…',async valid=>{await history(valid);if(valid())message('已读取当前工程最近的点表写回记录。');});
  async function poll(id,ticket,target){
    if(ticket!==generation||target!==key())return;
    try{const result=await request('status',{id},true);if(ticket!==generation||target!==key())return;renderRecords([result]);
      if(['completed','needs_review','failed','not_started'].includes(result.state)){message(result.state==='completed'?'点表写回、编译、回读和保存完成，请重新读取点表。':'执行未完成，请核对记录和备份。');return;}
      message('点表任务正在执行：'+result.state);pollTimer=setTimeout(()=>poll(id,ticket,target),1500);
    }catch(e){if(ticket===generation&&target===key())message('未取得最终结果：'+e.message+'。请刷新执行记录，不要重复提交。');}
  }
  el('tagReviewApply').onclick=()=>{sync();if(el('tagReviewApply').disabled)return;const id=plan.id,fingerprint=plan.fingerprint;work('正在提交已确认的点表方案…',async valid=>{
    // Disable this preview even when the response is lost; the same request is never replayed.
    plan=null;el('tagReviewConfirm').checked=false;const result=await request('apply',{id,fingerprint,confirm:true});if(valid()){message('已提交，正在执行…');poll(result.id,generation,key());}
  });};
  sync();setInterval(sync,750);
})();
