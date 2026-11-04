(() => {
  const el = id => document.getElementById(id);
  const modelApi = window.CylinderModel;
  const roleNames = {shared:'沿用公共输入', device:'按新名称替换 DB 成员', index:'替换数组第一维', input:'选择输入点', output:'选择输出点'};
  let snapshot = '', loaded = false, seq = 0, data, model, sample, rules, drafts, targetKey, labels, inputs;
  const message = text => { el('cyStatus').textContent = text; };
  const clearDraft = () => { drafts = null; el('cyResult').hidden = true; el('cyCode').textContent = ''; };
  function resetHost() {
    ++seq; data = model = sample = rules = null;
    el('cySample').replaceChildren(); el('cyRules').replaceChildren(); el('cySkipped').textContent = '';
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
      el('cySkipped').textContent=model.skipped.slice(0,12).join('\n')+(model.skipped.length>12?'\n另有 '+(model.skipped.length-12)+' 个不支持的网络':'');
      const sel=el('cySample');
      model.samples.forEach((s,i)=>sel.add(new Option(s.id+' · '+s.fb+' · 网络 '+s.network,String(i))));
      if (!model.samples.length) { message('没有找到可完整复用的直接 FB 调用，请换一个宿主块。'); return; }
      const last=getSaved(targetKey);
      const idx=model.samples.findIndex(s=>last && last.host===block && s.id===last.id && s.network===last.network);
      sel.selectedIndex=Math.max(0,idx); chooseSample();
    } catch(e) { if(ticket===seq) message(e.message); }
  }
  function chooseSample() {
    clearDraft(); sample=model.samples[el('cySample').selectedIndex];
    const saved=getSaved(templateKey());
    rules=Object.create(null); const t=table(['FB 参数','原接线','生成规则']);
    for(const p of sample.pins) {
      const choices=modelApi.roles(p,sample), tr=t.insertRow();
      addCell(tr,p.name+' · '+p.dir+' '+p.type); addCell(tr,p.binding.value);
      const sel=document.createElement('select'); choices.forEach(r=>sel.add(new Option(roleNames[r],r)));
      const joint=data.points.some(x=>x.symbol.toLowerCase()===p.binding.value.toLowerCase());
      const suggested=choices.includes('device')?'device':choices.includes('index')?'index':
        p.dir==='Input' && (!joint || /interlock|permit|互锁/i.test(p.name))?'shared':choices[0];
      sel.value=choices.includes(saved?.[p.name])?saved[p.name]:suggested;
      rules[p.name]=sel.value;
      sel.onchange=()=>{rules[p.name]=sel.value;el('cyBinding').hidden=true;clearDraft();message('规则已变更，请重新确认并保存模板。');};
      addCell(tr,'').appendChild(sel);
    }
    el('cyRules').replaceChildren(t); el('cySave').disabled=false;
    el('cyRulePanel').open=!saved; el('cyBinding').hidden=true;
    if (saved && sample.pins.every(p=>rules[p.name]===saved[p.name])) showBindings();
    else message('请核对样板规则，尤其是互锁输入与阀输出，然后保存模板。');
  }
  function showBindings() {
    clearDraft(); inputs=Object.create(null); labels=new Map(); const box=el('cyPoints'); box.replaceChildren();
    for (const area of ['I','Q']) {
      const list=document.createElement('datalist'); list.id='cyList'+area;
      for(const pt of data.points.filter(x=>x.area===area)) {
        let label=pt.address+' · '+pt.symbol;
        if(labels.has(label)) label+=' · '+pt.id;
        labels.set(label,pt.id); list.appendChild(new Option(pt.comment || pt.symbol,label));
      }
      box.appendChild(list);
    }
    for(const p of sample.pins.filter(p=>['input','output'].includes(rules[p.name]))) {
      const label=document.createElement('label'), input=document.createElement('input');
      label.textContent=p.name+' · '+roleNames[rules[p.name]]+' ';
      input.className='search-input'; input.setAttribute('list','cyList'+(rules[p.name]==='input'?'I':'Q'));
      input.placeholder='搜索地址或符号，选择匹配点位'; input.oninput=clearDraft;
      inputs[p.name]=input; label.appendChild(input); box.appendChild(label);
    }
    el('cyAlarmLabel').hidden=!Object.values(rules).includes('index'); el('cyBinding').hidden=false;
    message('模板已就绪。填写新气缸名称和专用点位后生成草稿。');
  }
  el('cyHost').onchange=loadHost; el('cySample').onchange=chooseSample;
  el('cySave').onclick=()=>{
    if (!Object.values(rules).includes('output') || !Object.values(rules).includes('input') || Object.values(rules).includes('')) {
      message('请至少指定一个输入点和一个输出点，并为全部参数选择规则。'); return;
    }
    try { localStorage.setItem(templateKey(),JSON.stringify(rules)); localStorage.setItem(targetKey,JSON.stringify({host:el('cyHost').value,id:sample.id,network:sample.network})); }
    catch(e) { message('当前浏览器无法保存模板：'+e.message); return; }
    el('cyRulePanel').open=false; showBindings();
  };
  el('cyName').oninput=el('cyAlarm').oninput=clearDraft;
  el('cyGenerate').onclick=()=>{
    clearDraft();
    try {
      if(snapshot!==window.cylinderIoState().id) throw new Error('工程快照已变更，请重新读取');
      const name=el('cyName').value.trim(), bindings=Object.fromEntries(Object.entries(inputs).map(([pin,input])=>[pin,labels.get(input.value)]));
      const out=modelApi.generate(data,model,sample,rules,name,bindings,el('cyAlarm').value.trim());
      const t=table(['参数','样板接线','新接线','规则']);
      out.diff.forEach(d=>{const tr=t.insertRow();[d.pin,d.before,d.after,roleNames[d.rule]].forEach(v=>addCell(tr,v));});
      el('cyDiff').replaceChildren(t);
      const notes=['生成自 '+data.host+' / 网络 '+sample.network+' / '+sample.id+'，快照 '+data.readAt,
        '在宿主 FB 添加 SCL 网络并核对以下声明。冲突检查覆盖当前快照能识别的显式写入；请复核互锁、报警、输出归属及 DB 初始值，再在博途中编译。',
        ...out.declarations,...out.warnings];
      drafts={name, text:notes.map(n=>'// '+String(n).replace(/[\r\n]+/g,' ')).join('\r\n')+'\r\n\r\n'+out.code+'\r\n'};
      el('cyWarnings').textContent=notes.join('\n'); el('cyCode').textContent=out.code;
      el('cyResult').hidden=false; message('已生成调用草稿，可核对差异并下载。');
    } catch(e) {message('未生成：'+e.message);}
  };
  el('cyDownload').onclick=()=>{
    if(!drafts || snapshot!==window.cylinderIoState().id) return;
    const url=URL.createObjectURL(new Blob(['\uFEFF',drafts.text],{type:'text/plain;charset=utf-8'}));
    const a=document.createElement('a'); a.href=url; a.download=drafts.name+'-调用草稿.scl'; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
})();
