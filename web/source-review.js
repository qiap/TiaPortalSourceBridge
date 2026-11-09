(function(){
  'use strict';
  const $=id=>document.getElementById(id);
  const el=(tag,text)=>{const n=document.createElement(tag);n.textContent=text==null?'':String(text);return n;};
  let generation=0,scope='',busy=false,report=null;
  const target=()=>({project_path:currentStatus.active_project||currentStatus.project_path||'',plc_name:currentStatus.target_plc||'',target_token:currentStatus.target_token||''});
  const key=()=>JSON.stringify(target());
  function controls(){['reviewCapture','reviewRefresh','reviewCompare','reviewDocument'].forEach(id=>$(id).disabled=busy||!target().target_token);$('reviewBefore').disabled=$('reviewAfter').disabled=busy;}
  function invalidate(message){generation++;report=null;$('reviewResults').replaceChildren();$('reviewStatus').textContent=message;}
  window.reviewSync=function(){if(scope===key())return;scope=key();invalidate('请选择或保存当前工程的源码快照。');$('reviewBefore').replaceChildren();$('reviewAfter').replaceChildren();controls();};
  async function request(action,data={},get=false){
    const payload=Object.assign(target(),data);
    if(!payload.target_token)throw Error('请等待工程与 PLC 识别完成。');
    const res=await fetch('/api/io/review/'+action+(get?'?'+new URLSearchParams(payload):''),get?{cache:'no-store'}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    if(action==='document'&&res.ok)return res.blob();
    const result=await res.json();if(!res.ok||result.error)throw Error(result.error||'操作失败');return result;
  }
  async function run(message,work){
    window.reviewSync();if(busy)return;busy=true;controls();invalidate(message);const ticket=generation,current=key();
    const valid=()=>ticket===generation&&current===key();
    try{await work(valid);}catch(e){if(valid())$('reviewStatus').textContent='操作失败：'+e.message;}finally{busy=false;controls();}
  }
  function fill(items,preferred){
    for(const id of ['reviewBefore','reviewAfter']){
      const selected=$(id).value;$(id).replaceChildren(new Option('请选择快照',''));
      items.forEach(a=>$(id).append(new Option(a.label+' · '+a.created_utc+' · '+a.count+' 个对象'+(a.warnings?'（读取不完整）':''),a.id)));
      if(items.some(a=>a.id===selected))$(id).value=selected;
    }
    if(preferred)$('reviewAfter').value=preferred;
  }
  window.reviewRefresh=()=>run('正在读取源码快照列表…',async valid=>{const items=await request('list',{},true);if(valid()){fill(items);$('reviewStatus').textContent=items.length?'已读取 '+items.length+' 份快照。':'当前工程与 PLC 尚未保存源码快照。';}});
  $('reviewRefresh').addEventListener('click',window.reviewRefresh);
  $('reviewCapture').addEventListener('click',()=>run('正在保存已读取的源码快照…',async valid=>{
    const saved=await request('capture',{label:$('reviewLabel').value.trim()});if(!valid())return;
    const items=await request('list',{},true);if(!valid())return;fill(items,saved.id);
    $('reviewStatus').textContent='已保存 '+saved.count+' 个源码对象。'+saved.warnings.join('；');
  }));
  ['reviewBefore','reviewAfter'].forEach(id=>$(id).addEventListener('change',()=>invalidate('快照选择已改变，请重新比较。')));
  $('reviewCompare').addEventListener('click',()=>run('正在比较结构并追踪已解析调用方…',async valid=>{
    if(!$('reviewBefore').value||!$('reviewAfter').value)throw Error('请选择之前与之后的源码快照。');
    const data=await request('compare',{before_id:$('reviewBefore').value,after_id:$('reviewAfter').value});if(!valid())return;
    report=data;$('reviewStatus').textContent='变化对象 '+data.objects.length+' 个；仅导出噪声 '+data.export_noise_only+' 个；显式调用影响路径 '+data.impact.length+' 条。';render();
  }));
  $('reviewDocument').addEventListener('click',()=>run('正在生成交付资料…',async valid=>{
    const id=$('reviewAfter').value;if(!id)throw Error('请选择之后的源码快照。');
    const blob=await request('document',{id},true);if(!valid())return;
    const url=URL.createObjectURL(blob),a=el('a','');a.href=url;a.download='TIA-Documentation-'+id+'.zip';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    $('reviewStatus').textContent='交付资料已导出，包含源码目录、调用关系、覆盖说明及源码。';
  }));
  $('reviewSearch').addEventListener('input',render);
  function render(){
    if(!report)return;const box=$('reviewResults');box.replaceChildren();
    const warnings=el('details','');warnings.append(el('summary','覆盖说明（请先核对）'),el('p',report.warnings.join('\n')));box.append(warnings);
    const search=$('reviewSearch').value.trim().toLowerCase();
    const filtered=report.objects.filter(row=>[row.name,row.group,row.unit,row.kind,row.type].join(' ').toLowerCase().includes(search));
    let shown=0;const more=el('button','加载更多对象');more.className='btn';
    function append(){more.remove();filtered.slice(shown,shown+100).forEach(row=>{
      const card=el('details','');card.style.cssText='padding:12px;border-bottom:1px solid var(--card-border);';
      const kind={added:'新增',removed:'移除',modified:'修改',only_before:'仅之前快照',only_after:'仅之后快照'}[row.type]||row.type;
      card.append(el('summary',kind+' · '+row.name+' · '+row.unit+' / '+row.group+' · '+row.changes+' 项结构变化'));
      const impacted=report.impact.filter(i=>i.changed===row.id);card.append(el('p','已识别调用方：'+(impacted.map(i=>i.caller_name+'（'+(i.side==='before'?'之前':'之后')+'，'+(i.path.length-1)+' 层）').slice(0,100).join('、')||'未发现；不代表没有影响')));
      if(impacted.length>100)card.append(el('p','调用方展示前 100 项，共 '+impacted.length+' 项。'));
      const button=el('button','查看对象变化');button.className='btn btn-sm';const detail=el('div','');card.append(button,detail);
      button.addEventListener('click',async()=>{
        const ticket=generation,current=key(),selected=report;button.disabled=true;detail.textContent='正在读取…';
        try{const result=await request('object',{before_id:selected.before_id,after_id:selected.after_id,object_id:row.id});if(ticket!==generation||current!==key()||selected!==report)return;
          detail.replaceChildren(el('p',result.semantic.warnings.join('；')));
          if(result.semantic.status==='unsupported')detail.append(el('p','此对象按原始文本比较。'));
          const raw=el('details','');raw.append(el('summary','原始文本（按需展开，前 1000 行）'));const pre=el('pre',result.lines.slice(0,1000).map(l=>(l.type==='add'?'+ ':l.type==='delete'?'- ':'  ')+l.text).join('\n'));pre.style.whiteSpace='pre-wrap';raw.append(pre);
          result.semantic.changes.slice(0,100).forEach(c=>{const item=el('details','');item.append(el('summary',c.type+' · '+c.path),el('pre','之前\n'+(c.before||'（无）')+'\n之后\n'+(c.after||'（无）')));detail.append(item);});
          if(result.semantic.changes.length>100)detail.append(el('p','已展示前 100 项，共 '+result.semantic.changes.length+' 项；完整源码在交付资料中。'));detail.append(raw);
        }catch(e){if(ticket===generation&&current===key())detail.textContent='读取失败：'+e.message;}finally{button.disabled=false;}
      });box.append(card);
    });shown=Math.min(shown+100,filtered.length);if(shown<filtered.length){more.textContent='加载更多（'+shown+'/'+filtered.length+'）';box.append(more);}}
    more.addEventListener('click',append);append();
  }
  controls();
})();
