(() => {
  const el = id => document.getElementById(id);
  const modelApi = window.CylinderModel;
  const roleNames = {shared:'沿用公共输入', device:'按新名称替换 DB 成员', index:'替换数组第一维', input:'选择输入点', output:'选择输出点', open:'保持未连接', feedback:'跟随对应的新阀输出'};
  let snapshot = '', loaded = false, seq = 0, data, model, sample, rules, drafts, targetKey, inputs;
  let ioSuffixes={};
  const deviceKind=()=>el('cyDeviceKind')?.value||'cylinder';
  const activeSuffixes=()=>deviceKind()==='cylinder'?{}:Object.fromEntries(Object.entries(ioSuffixes).filter(([pin])=>['input','output'].includes(rules?.[pin])));
  function loadDeviceConfig(record){if(el('cyDeviceKind'))el('cyDeviceKind').value=['cylinder','motor','valve','vacuum','servo'].includes(record?.device_kind)?record.device_kind:'cylinder';ioSuffixes=record?.io_suffixes&&typeof record.io_suffixes==='object'&&!Array.isArray(record.io_suffixes)?{...record.io_suffixes}:{};}
  let templateContext=null, templateRecord=null, templateConfirmed=false, templateBusy=false, sampleTicket=0;
  const message = text => { el('cyStatus').textContent = text; };
  const clearDraft = () => { drafts = null; el('cyResult').hidden = true; el('cyCode').textContent = ''; };
  function resetHost() {
    ++seq; data = model = sample = rules = inputs = null;
    ++sampleTicket; templateContext=templateRecord=null;templateConfirmed=false;templateBusy=false;
    el('cyTemplateExport').disabled=el('cyTemplateImport').disabled=true;el('cyTemplateStatus').textContent='';
    el('cySample').replaceChildren(); el('cyRules').replaceChildren(); el('cySkipped').textContent = '';
    el('cySummary').textContent = ''; el('cyCandidates').replaceChildren();
    el('cyRulePanel').hidden = true; el('cyOpenNotice').hidden = true;
    el('cyBinding').hidden = true; el('cySave').disabled = true; clearDraft();
  }
  async function api(path, params) {
    const r = await fetch('/api/io/' + path + '?' + new URLSearchParams({id:snapshot,...params}),{cache:'no-store'});
    const result = await r.json();
    if (!r.ok) throw new Error(result.error || '请求失败');
    return result;
  }
  const getSaved = key => { try { return JSON.parse(localStorage.getItem(key)); } catch (_) { return null; } };
  const templateKey = () => JSON.stringify([targetKey,el('cyHost').value,data.hash,sample.id,sample.network]);
  const templateStatus = value => {el('cyTemplateStatus').textContent=value;};
  const templateRequest = () => ({id:snapshot,host:el('cyHost').value,sample_id:sample.id,sample_network:sample.network});
  function editedRules() {
    templateConfirmed=false;el('cyTemplateExport').disabled=true;el('cyBinding').hidden=true;clearDraft();
    templateStatus('规则尚未保存。请核对当前样板的实际接线，再确认并保存。');
  }
  const table = headings => {
    const t = document.createElement('table'); t.className = 'execution-table';
    const row = t.createTHead().insertRow();
    headings.forEach(h => { const th=document.createElement('th'); th.textContent=h; row.appendChild(th); });
    return t;
  };
  const addCell = (tr,s) => { const c=tr.insertCell(); c.textContent=s; return c; };
  window.cylinderSync = () => {
    const s = window.cylinderIoState();
    el('cyTarget').textContent = [s.target.project_path,s.target.plc_name].filter(Boolean).join(' · ') || '请先选择工程和 PLC';
    if (window.cylinderApplying) { el('cyCached').disabled = el('cyLive').disabled = true; return; }
    el('cyCached').disabled = el('cyLive').disabled = s.busy || !s.target.target_token || !s.target.plc_name;
    if (snapshot !== s.id || !s.id) {
      if (snapshot || loaded) resetHost();
      snapshot=s.id; loaded=false; el('cyEditor').hidden=true;
      if (!snapshot) {
        const note=el('ioMessage').textContent;
        message(!s.target.plc_name ? '请先在控制中心选择工程和 PLC。' : s.busy || note.includes('读取失败') ? note : '请读取当前 PLC 的源码和 I/O 点位。');
        return;
      }
    }
    if (loaded || !el('tab-cylinder').classList.contains('active')) return;
    targetKey = JSON.stringify(['cylinder-v1',s.target.project_path.toLowerCase(),s.target.plc_name]);
    loaded=true; loadBlocks();
  };
  async function read(mode) {
    await window.ioRead(mode); window.cylinderSync();
    if (!window.cylinderIoState().id) message(el('ioMessage').textContent);
  }
  el('cyCached').onclick=()=>read('source'); el('cyLive').onclick=()=>read('live');
  async function loadBlocks() {
    const ticket=++seq;
    try {
      message('正在查找当前 PLC 的源码块…');
      const list = await api('cylinder-blocks',{});
      if (ticket!==seq) return;
      const sel=el('cyHost'); sel.replaceChildren(new Option('选择包含气缸调用的宿主块',''));
      const priority=b=>/气缸|cylind|zylinder/i.test(b.name)?0:1;
      list.blocks.sort((a,b)=>priority(a)-priority(b)||a.name.localeCompare(b.name));
      list.blocks.forEach(b=>sel.add(new Option([b.unit_name,b.group,b.name].filter(Boolean).join(' / '),b.id)));
      el('cyEditor').hidden=false;
      message(list.blocks.length ? '选择宿主块，提取其中的气缸调用。' + (list.warnings ? ' 本次读取有警告，详见 I/O 表。' : '') : '本次快照缺少程序块内容，请从博途重新读取。');
      const last=getSaved(targetKey);
      if (last && list.blocks.some(b=>b.id===last.host)) { sel.value=last.host; await loadHost(); }
    } catch(e) { if(ticket===seq) message(e.message); }
  }
  async function loadHost() {
    resetHost(); const ticket=seq, block=el('cyHost').value;
    if(!block) {message('请选择宿主程序块。'); return;}
    try {
      message('正在提取样板及对应软件单元的点位…');
      const result=await api('cylinder-template',{block});
      if(ticket!==seq) return;
      data=result; model=modelApi.extract(data);
      const sel=el('cySample');
      const candidates=model.candidates;
      el('cySummary').textContent='当前宿主共 '+candidates.length+' 个调用实例，'+model.samples.length+' 个可生成，'+(candidates.length-model.samples.length)+' 个暂不支持。';
      const t=table(['实例','功能块','网络','状态与说明','操作']);
      candidates.forEach((s,i)=>{
        const state=s.supported?'可生成':'暂不支持';
        sel.add(new Option(s.id+' · '+s.fb+' · 网络 '+s.network+' · '+state,String(i)));
        const tr=t.insertRow(); [s.id,s.fb,s.network].forEach(v=>addCell(tr,v));
        const open=s.supported?s.pins.filter(p=>p.binding.open).map(p=>p.name):[];
        addCell(tr,s.supported ? state+(open.length?'，保留未连接参数 '+open.join('、'):'') : state+' — '+s.reason);
        const b=document.createElement('button'); b.className='btn'; b.textContent='查看';
        b.onclick=()=>{sel.selectedIndex=i;chooseSample();}; addCell(tr,'').appendChild(b);
      });
      el('cyCandidates').replaceChildren(t);
      if (!candidates.length) { message('当前宿主没有可识别的块调用实例。'); return; }
      const last=getSaved(targetKey);
      const idx=candidates.findIndex(s=>last && last.host===block && s.id===last.id && s.network===last.network);
      sel.selectedIndex=idx>=0?idx:Math.max(0,candidates.findIndex(s=>s.supported)); chooseSample();
    } catch(e) { if(ticket===seq) message(e.message); }
  }
  async function chooseSample() {
    const ticket=++sampleTicket;
    clearDraft(); sample=model.candidates[el('cySample').selectedIndex]; rules=null;
    templateContext=templateRecord=null;templateConfirmed=false;templateBusy=false;
    el('cyTemplateExport').disabled=el('cyTemplateImport').disabled=true;templateStatus('');
    el('cyRules').replaceChildren(); el('cyBinding').hidden=true; el('cySave').disabled=true;
    el('cyRulePanel').hidden=true; el('cyOpenNotice').hidden=true;
    if(!sample) return;
    if(!sample.supported) {
      el('cySkipped').textContent=sample.id+' · 网络 '+sample.network+'\n'+sample.reason;
      message('该实例已列出，暂不能作为生成模板。原有逻辑需完整保留。'); return;
    }
    el('cySkipped').textContent=''; el('cyRulePanel').hidden=false;
    const open=sample.pins.filter(p=>p.binding.open);
    if(open.length) {
      el('cyOpenNotice').hidden=false;
      el('cyOpenNotice').textContent='未连接参数 '+open.map(p=>p.name).join('、')+' 将保持未连接，草稿中省略这些参数。写入时复制样板的声明及初始配置，不复制在线实际值。';
    }
    templateStatus('正在读取当前项目保存的模板规则…');
    try {
      const result=await api('cylinder-templates',templateRequest());
      if(ticket!==sampleTicket)return;
      if(!result.context || !/^[0-9a-f]{64}$/i.test(result.context.sample_fingerprint || ''))throw new Error('样板核对信息不完整');
      templateContext=result.context;templateRecord=result.template || null;
      const legacy=!templateRecord?getSaved(templateKey()):null;
      loadDeviceConfig(templateRecord);
      const saved=templateRecord?.rules || legacy;
      const valid=renderRules(saved);
      templateConfirmed=result.matches===true && valid;
      el('cySave').disabled=false;el('cyTemplateImport').disabled=false;el('cyTemplateExport').disabled=!templateConfirmed;
      el('cyRulePanel').open=!templateConfirmed;
      if(templateConfirmed){showBindings();templateStatus('已载入项目模板。当前样板接线、实例声明及 FB 接口核对一致。');}
      else if(templateRecord)templateStatus('样板或 FB 接口已变化，已载入旧规则供对照。请逐项核对当前接线后重新保存。');
      else if(legacy)templateStatus('找到与当前宿主版本匹配的浏览器旧规则。核对后保存，将迁移为项目模板。');
      else templateStatus('尚无项目模板。请核对规则后保存；保存内容可导出到其他电脑。');
    }catch(error){if(ticket===sampleTicket){renderRules(null);el('cySave').disabled=true;templateStatus('无法读取项目模板：'+error.message);}}
  }
  function renderRules(saved) {
    rules=Object.create(null); const t=table(['FB 参数','原接线','生成规则','选点用途后缀']);
    for(const p of sample.pins) {
      const choices=modelApi.roles(p,sample), tr=t.insertRow();
      addCell(tr,p.name+' · '+p.dir+' '+p.type); addCell(tr,p.binding.value);
      const sel=document.createElement('select'); choices.forEach(r=>sel.add(new Option(roleNames[r],r)));
      const joint=data.points.some(x=>x.symbol.toLowerCase()===p.binding.value.toLowerCase());
      const suggested=choices.includes('open')?'open':choices.includes('feedback')?'feedback':choices.includes('device')?'device':choices.includes('index')?'index':
        p.dir==='Input' && (!joint || /interlock|permit|互锁/i.test(p.name))?'shared':choices[0];
      sel.value=choices.includes(saved?.[p.name])?saved[p.name]:suggested;
      rules[p.name]=sel.value;
      sel.dataset.templatePin=p.name;
      sel.onchange=()=>{rules[p.name]=sel.value;editedRules();message('规则已变更，请重新确认并保存模板。');};
      addCell(tr,'').appendChild(sel);
      const suffix=document.createElement('input');suffix.value=typeof ioSuffixes[p.name]==='string'?ioSuffixes[p.name]:'';suffix.placeholder='如：运行反馈、启动输出';suffix.maxLength=24;suffix.setAttribute('aria-label',p.name+' 用途后缀');suffix.disabled=deviceKind()==='cylinder';suffix.oninput=()=>{ioSuffixes[p.name]=suffix.value;editedRules();};addCell(tr,'').append(suffix);
    }
    el('cyRules').replaceChildren(t);el('cyBinding').hidden=true;
    message('请核对样板规则，尤其是互锁输入与阀输出，然后保存模板。');
    return !!saved && Object.keys(saved).length===sample.pins.length && sample.pins.every(p=>rules[p.name]===saved[p.name]);
  }
  function showBindings() {
    clearDraft(); inputs=Object.create(null); const box=el('cyPoints'); box.replaceChildren();
    for(const p of sample.pins.filter(p=>['input','output'].includes(rules[p.name]))) {
      const field=document.createElement('div'), label=document.createElement('label'), search=document.createElement('input'), select=document.createElement('select'), note=document.createElement('small');
      label.textContent=p.name+' · '+roleNames[rules[p.name]];
      search.className=select.className='search-input'; search.placeholder='筛选空闲点位：地址或名称';
      search.setAttribute('aria-label',p.name+' 点位筛选');select.setAttribute('aria-label',p.name+' 空闲点位');
      select.dataset.pin=p.name; select.dataset.area=rules[p.name]==='input'?'I':'Q';
      field.style.cssText='display:grid;gap:6px;min-width:0'; select.style.width='100%';note.style.cssText='color:var(--text-muted);overflow-wrap:anywhere';
      inputs[p.name]={select,search,note,options:modelApi.availablePoints(data,model,sample,select.dataset.area)};
      label.appendChild(select);field.append(label,search,note);box.appendChild(field);
      search.oninput=()=>renderPointOptions();
      select.onchange=()=>{clearDraft();renderPointOptions();previewNaming();};
    }
    const available={I:modelApi.availablePoints(data,model,sample,'I').length,Q:modelApi.availablePoints(data,model,sample,'Q').length};
    el('cyAvailability').textContent='当前读取范围内未发现占用：输入 '+available.I+' 个，输出 '+available.Q+' 个。同工位已有气缸末尾之后的点位优先列出，请自行选择。'+(data.usage_coverage?.note || '未获得完整占用信息的点位不提供选择。');
    renderPointOptions();previewNaming();
    el('cyAlarmLabel').hidden=!Object.values(rules).includes('index'); el('cyBinding').hidden=false;
    message('模板已就绪。填写气缸名称、程序实例名并选择空闲点位，即可生成并写入博途，也可以先预览。');
  }
  function selectedBindings() {
    return Object.fromEntries(Object.entries(inputs || {}).map(([pin,field])=>[pin,field.select.value]));
  }
  function renderPointOptions() {
    const selected=selectedBindings();
    for(const [pin,field] of Object.entries(inputs)) {
      const current=field.select.value, query=field.search.value.trim().toLowerCase();
      const taken=new Set(Object.entries(selected).filter(([p,id])=>p!==pin && id).map(([,id])=>modelApi.pointAddress(data.points.find(p=>p.id===id)?.address)));
      const available=field.options.filter(p=>!taken.has(p.address_key));
      const matches=available.filter(p=>p.id===current || [p.address,p.symbol,p.comment].join(' ').toLowerCase().includes(query));
      field.select.replaceChildren(new Option(available.length?'请选择空闲点位':'当前没有可选择的空闲点位',''));
      for(const recommended of [true,false]) {
        const rows=matches.filter(p=>p.recommended===recommended);if(!rows.length)continue;
        const group=document.createElement('optgroup');group.label=recommended?'推荐：已有气缸点位之后':'其他空闲点位';
        rows.forEach(p=>group.appendChild(new Option(p.address+' · '+p.symbol,p.id)));field.select.appendChild(group);
      }
      field.select.value=matches.some(p=>p.id===current)?current:'';
      const point=field.options.find(p=>p.id===field.select.value),anchor=field.options.find(p=>p.after_address)?.after_address;
      field.note.textContent=point ? point.recommendation || '当前读取范围内未发现占用' : anchor ? '已有气缸最后点位 '+anchor+'，优先选择后续空闲点。' : '按地址顺序列出空闲点；已选点不会重复分配。';
    }
  }
  function namingPlan() { return window.CylinderNaming.plan(data,sample,rules,selectedBindings(),el('cyDisplayName').value.trim(),deviceKind(),activeSuffixes()); }
  function previewNaming() {
    el('cyNamingPreview').replaceChildren();
    if(!el('cyDisplayName').value.trim()) {el('cyNamingStatus').textContent='请填写气缸名称，选择点位后自动生成输入和输出名称。';return;}
    try {
      const plan=namingPlan(),t=table(['用途','选用点位','当前名称','自动生成名称']);
      plan.renames.forEach(r=>{const row=t.insertRow();[r.pin,r.address,r.old_symbol,r.new_symbol].forEach(v=>addCell(row,v));});
      el('cyNamingPreview').replaceChildren(t);el('cyNamingStatus').textContent='按样板的伸出、缩回命名规则生成。这里预览点位改名和调用草稿，尚未改动博途工程。';
    }catch(e){el('cyNamingStatus').textContent=e.message;}
  }
  if(el('cyDeviceKind'))el('cyDeviceKind').onchange=()=>{if(sample){const previous={...rules};renderRules(previous);editedRules();}};
  el('cyHost').onchange=loadHost; el('cySample').onchange=chooseSample;
  el('cySave').onclick=async()=>{
    if(!sample?.supported || !rules || !templateContext || templateBusy || window.cylinderApplying) return;
    if (!Object.values(rules).includes('output') || Object.values(rules).includes('')) {
      message('请至少指定一个阀输出点，并为全部参数选择规则。未连接的输入可保持原样。'); return;
    }
    const ticket=sampleTicket,legacyKey=templateKey(),payload={...templateRequest(),sample_fingerprint:templateContext.sample_fingerprint,rules:{...rules},device_kind:deviceKind(),io_suffixes:activeSuffixes(),reviewed:true};
    templateBusy=true;el('cySave').disabled=true;templateStatus('正在保存项目模板规则…');
    try {
      const response=await fetch('/api/io/cylinder-templates',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload),cache:'no-store'});
      const result=await response.json();
      if(!response.ok || result.matches!==true || !result.template)throw new Error(result.error || '项目模板未保存');
      if(ticket!==sampleTicket)return;
      if(JSON.stringify(payload.rules)!==JSON.stringify(rules)||payload.device_kind!==deviceKind()||JSON.stringify(payload.io_suffixes)!==JSON.stringify(activeSuffixes())){templateRecord=result.template;editedRules();templateStatus('上次确认的规则已保存，但页面规则随后又发生变化。请核对并重新保存当前规则。');return;}
      templateContext=result.context;templateRecord=result.template;templateConfirmed=true;el('cyTemplateExport').disabled=false;
      try{localStorage.setItem(targetKey,JSON.stringify({host:el('cyHost').value,id:sample.id,network:sample.network}));localStorage.removeItem(legacyKey);}catch(_){}
      el('cyRulePanel').open=false;showBindings();templateStatus('项目模板已保存到本机，可导出模板规则迁移到其他电脑。');
    }catch(error){if(ticket===sampleTicket){templateConfirmed=false;el('cyTemplateExport').disabled=true;templateStatus('保存失败：'+error.message);}}
    finally{if(ticket===sampleTicket){templateBusy=false;el('cySave').disabled=false;}}
  };
  el('cyTemplateExport').onclick=()=>{
    if(!templateRecord || !templateConfirmed)return;
    const url=URL.createObjectURL(new Blob([JSON.stringify(templateRecord,null,2)],{type:'application/json;charset=utf-8'}));
    const a=document.createElement('a');a.href=url;a.download=(data.host+'-'+sample.id).replace(/[<>:"/\\|?*\u0000-\u001f]/g,'_')+'-模板规则.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
  el('cyTemplateImport').onclick=()=>el('cyTemplateFile').click();
  el('cyTemplateFile').onchange=async()=>{
    const file=el('cyTemplateFile').files[0],ticket=sampleTicket;el('cyTemplateFile').value='';
    if(!file || !sample?.supported || !templateContext || templateBusy)return;
    try {
      if(file.size>65536)throw new Error('模板文件超过 64 KB');
      const imported=JSON.parse((await file.text()).replace(/^\uFEFF/,''));
      if(ticket!==sampleTicket)return;
      if(![1,2].includes(imported.schema) || imported.kind!=='cylinder-template-rules' || !imported.rules || Array.isArray(imported.rules) || typeof imported.rules!=='object' ||
        Object.keys(imported.rules).length>512 || Object.values(imported.rules).some(value=>typeof value!=='string' || !Object.hasOwn(roleNames,value)))
        throw new Error('请选择导出的“模板规则”文件，生成方案和 SCL 草稿不能作为模板导入');
      loadDeviceConfig(imported);const exact=renderRules(imported.rules);editedRules();el('cyRulePanel').open=true;
      templateStatus('已载入模板规则，尚未保存。来源：'+[imported.host_name,imported.sample_id,imported.fb].filter(Boolean).join(' / ')+
        '。所有 I/O、DB 和公共输入接线均以当前样板为准，请重新核对后保存。'+(exact?'':' 部分参数不匹配，已保留当前样板的默认建议，请重点检查。'));
    }catch(error){if(ticket===sampleTicket)templateStatus('未导入：'+error.message);}
  };
  el('cyName').oninput=el('cyAlarm').oninput=clearDraft;
  el('cyDisplayName').oninput=()=>{clearDraft();if(sample?.supported && inputs)previewNaming();};
  function generateDraft() {
    clearDraft();
    try {
      if(!sample?.supported || !rules || !templateConfirmed) throw new Error('请选择可生成的实例，并确认保存当前项目模板');
      if(snapshot!==window.cylinderIoState().id) throw new Error('工程快照已变更，请重新读取');
      const name=el('cyName').value.trim(), displayName=el('cyDisplayName').value.trim(),bindings=selectedBindings();
      const plan=namingPlan();
      const out=modelApi.generate({...data,points:plan.points},model,sample,rules,name,bindings,el('cyAlarm').value.trim());
      const t=table(['参数','样板接线','新接线','规则']);
      out.diff.forEach(d=>{const tr=t.insertRow();[d.pin,d.before,d.after,roleNames[d.rule]].forEach(v=>addCell(tr,v));});
      el('cyDiff').replaceChildren(t);
      const notes=['气缸名称：'+displayName+'；程序实例：'+name,
        '生成自 '+data.host+' / 网络 '+sample.network+' / '+sample.id+'，快照 '+data.readAt,
        '写入时沿用样板的 LAD 网络，添加新实例及所需声明，并同步修改点位名称。下方 SCL 仅用于核对接线；写入前会从博途重新读取并检查占用。',
        ...plan.renames.map(r=>'点位命名 '+r.address+'：'+r.old_symbol+' → '+r.new_symbol),...plan.warnings,...out.declarations,...out.warnings];
      drafts={name, text:notes.map(n=>'// '+String(n).replace(/[\r\n]+/g,' ')).join('\r\n')+'\r\n\r\n'+out.code+'\r\n',
        plan:{schema:1,kind:'cylinder-draft',project:data.project,plc:data.plc,host:data.host,source_hash:data.hash,read_at:data.readAt,
          cylinder_name:displayName,instance_name:name,renames:plan.renames,declarations:out.declarations,call:out.code,warnings:[...plan.warnings,...out.warnings],applied:false}};
      el('cyWarnings').textContent=notes.join('\n'); el('cyCode').textContent=out.code;
      el('cyResult').hidden=false; message('已生成预览。点击“生成并写入博途”将执行写入、编译和保存。');
      return true;
    } catch(e) {message('未生成：'+e.message); return false;}
  }
  el('cyGenerate').onclick=generateDraft;
  window.cylinderApplyRequest=()=>{
    if(!generateDraft()) return null;
    const state=window.cylinderIoState();
    if(!state.target.target_token || state.id!==snapshot) { message('目标已变更，请重新读取工程。'); return null; }
    return {id:snapshot,host:el('cyHost').value,sample_id:sample.id,sample_network:sample.network,
      source_hash:data.hash,cylinder_name:el('cyDisplayName').value.trim(),instance_name:el('cyName').value.trim(),
      alarm_index:el('cyAlarm').value.trim(),device_kind:deviceKind(),io_suffixes:activeSuffixes(),rules:{...rules},bindings:selectedBindings(),...state.target};
  };
  window.cylinderBatchContext=()=>({snapshot,id:snapshot,target:{...window.cylinderIoState().target},host:el('cyHost').value,sample_id:sample?.id,sample_network:sample?.network,source_hash:data?.hash,rules:rules?{...rules}:null,device_kind:deviceKind(),io_suffixes:activeSuffixes(),points:(data?.points||[]).map(p=>({...p})),template_confirmed:templateConfirmed});
  el('cyDownload').onclick=()=>{
    if(!drafts || snapshot!==window.cylinderIoState().id) return;
    const url=URL.createObjectURL(new Blob(['\uFEFF',drafts.text],{type:'text/plain;charset=utf-8'}));
    const a=document.createElement('a'); a.href=url; a.download=drafts.name+'-调用草稿.scl'; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
  el('cyDownloadPlan').onclick=()=>{
    if(!drafts || snapshot!==window.cylinderIoState().id) return;
    const url=URL.createObjectURL(new Blob([JSON.stringify(drafts.plan,null,2)],{type:'application/json;charset=utf-8'}));
    const a=document.createElement('a');a.href=url;a.download=drafts.name+'-气缸生成方案.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
})();
