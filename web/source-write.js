(() => {
  'use strict';const el=id=>document.getElementById(id),node=(tag,text)=>{const n=document.createElement(tag);n.textContent=text??'';return n;};if(!el('sourceWriteApply'))return;
  const target=()=>({project_path:window.currentStatus?.active_project||window.currentStatus?.project_path||'',plc_name:window.currentStatus?.target_plc||'',target_token:window.currentStatus?.target_token||''});
  const key=()=>JSON.stringify([target().project_path,target().plc_name,el('reviewAfter').value]);let plan=null,busy=false,generation=0,scope='',timer=null;
  const message=text=>el('sourceWriteStatus').textContent=text;
  function sync(){
    if(scope!==key()){scope=key();generation++;plan=null;clearTimeout(timer);el('sourceWriteConfirm').checked=false;el('sourceWritePreview').replaceChildren();el('sourceWriteResult').textContent='';}
    const selected=!!el('reviewAfter').value&&!!target().target_token,blocked=busy||!!window.cylinderApplying||!!window.cylinderRecoveryPending;
    el('sourceTextExport').disabled=blocked||!selected;el('sourceTextPreview').disabled=blocked||!selected||!el('sourceTextFile').files.length;el('sourceParameterPreview').disabled=blocked||!selected||!el('sourceParameterId').value.trim();
    el('sourceWriteApply').disabled=blocked||!plan?.can_apply||!el('sourceWriteConfirm').checked||!/\.ap17$/i.test(target().project_path);for(const id of ['sourceWriteCheck','sourceWriteRestore','sourceWriteContinue'])el(id).disabled=busy||!target().target_token||!el('sourceWriteJobId').value.trim();
  }
  async function request(action,data={}){const response=await fetch('/api/io/source-write/'+action,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...target(),snapshot_id:el('reviewAfter').value,...data})});if(action==='text-export'&&response.ok)return response.blob();const result=await response.json();if(!response.ok)throw Error(result.error||'请求失败');return result;}
  async function work(label,fn){sync();if(busy)return;busy=true;sync();message(label);const ticket=generation,current=key();try{await fn(()=>ticket===generation&&current===key());}catch(e){if(ticket===generation&&current===key())message('操作失败：'+e.message);}finally{busy=false;sync();}}
  function show(data){
    plan=data;el('sourceWriteConfirm').checked=false;const box=el('sourceWritePreview');box.replaceChildren(node('p',data.purpose),node('p',data.note),node('p',data.limitation));
    for(const skipped of data.unmatched||[])box.append(node('p','未匹配：'+skipped));let count=0;
    for(const patch of data.patches){const detail=node('details','');detail.append(node('summary',patch.object_name+' · '+patch.group+' · '+patch.diff.changes.length+' 项'));
      for(const change of patch.diff.changes){if(++count>300)break;const item=node('details','');item.append(node('summary',change.type+' · '+change.path),node('pre','之前\n'+(change.before??'（无）')+'\n之后\n'+(change.after??'（无）')));detail.append(item);}box.append(detail);}
    if(count>300){plan=null;box.append(node('p','本批变化过多，请缩小回填范围，确保能够核对全部预览。'));}message('已生成预览，尚未修改工程。');
  }
  el('sourceTextExport').onclick=()=>work('正在导出已有语言文本…',async valid=>{const blob=await request('text-export');if(!valid())return;const url=URL.createObjectURL(blob),a=node('a','');a.href=url;a.download='TIA-Translations.xlsx';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);message('请修改 Translation，把需要回填的行标为 update，再上传预览。');});
  el('sourceTextFile').onchange=()=>{generation++;plan=null;el('sourceWriteConfirm').checked=false;sync();};
  el('sourceTextPreview').onclick=()=>work('正在校验翻译表身份与原文…',async valid=>{const file=el('sourceTextFile').files[0];if(!file||file.size>8*1024*1024)throw Error('请选择不超过 8 MB 的 .xlsx 文件。');const encoded=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result).split(',')[1]);reader.onerror=()=>reject(Error('无法读取翻译表'));reader.readAsDataURL(file);});const data=await request('text-preview',{file_name:file.name,content_base64:encoded});if(valid())show(data);});
  el('sourceParameterId').oninput=()=>{generation++;plan=null;el('sourceWriteConfirm').checked=false;sync();};el('sourceParameterPreview').onclick=()=>work('正在按符号和类型核对参数…',async valid=>{const data=await request('parameter-preview',{parameter_id:el('sourceParameterId').value.trim()});if(valid())show(data);});
  el('sourceWriteConfirm').onchange=sync;el('sourceWriteJobId').oninput=sync;
  async function poll(id,ticket,current){if(ticket!==generation||current!==key())return;try{const result=await request('status',{id});if(ticket!==generation||current!==key())return;el('sourceWriteResult').textContent=JSON.stringify(result,null,2);
    if(['completed','needs_review','failed','unknown','outcome_unknown'].includes(result.state)){message(result.state==='completed'?'写回、完整回读和保存完成，请重新读取源码。':'执行结果需要核对，请保留现场和备份；不会自动重放。');return;}timer=setTimeout(()=>poll(id,ticket,current),1500);
  }catch(e){if(ticket===generation)message('无法确认最终执行结果：'+e.message+'。请保留执行编号，不要重复提交。');}}
  el('sourceWriteApply').onclick=()=>{sync();if(el('sourceWriteApply').disabled)return;const selected=plan;work('正在提交源码写回…',async valid=>{plan=null;el('sourceWriteConfirm').checked=false;el('sourceWriteJobId').value=selected.id;const data=await request('apply',{id:selected.id,fingerprint:selected.fingerprint,confirm:true});if(valid())poll(data.id,generation,key());});};
  el('sourceWriteCheck').onclick=()=>work('正在查询源码执行记录…',async valid=>{if(valid())await poll(el('sourceWriteJobId').value.trim(),generation,key());});for(const [id,action] of [['sourceWriteRestore','restore-preview'],['sourceWriteContinue','continue-preview']])el(id).onclick=()=>work('Checking stored evidence...',async valid=>{const data=await request(action,{id:el('sourceWriteJobId').value.trim()});if(valid())show(data);});sync();setInterval(sync,750);
})();
