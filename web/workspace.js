(() => {
  'use strict';
  const $=id=>document.getElementById(id), n=(tag,text,cls)=>{const e=document.createElement(tag);if(text!=null)e.textContent=text;if(cls)e.className=cls;return e;};
  const target=()=>({project_path:window.currentStatus?.active_project||window.currentStatus?.project_path||'',plc_name:window.currentStatus?.target_plc||'',target_token:window.currentStatus?.target_token||''});
  const labels={preview:'待核对方案',accepted:'已提交，等待结果',running:'执行中',checking:'正在核对',importing:'正在导入',verifying:'正在回读',saving:'正在保存',alarms:'正在处理报警',texts:'正在处理文本',compiling:'正在编译',completed:'执行完成',verified_unsaved:'已验证（未保存，未下载）',preflight_ready:'下载预检通过',blocked:'预检阻塞',failed:'执行失败',needs_review:'部分完成或结果待核对',needs_review_unknown_outcome:'结果待核对',unknown:'结果未确认',outcome_unknown:'结果未确认',not_started:'未开始',stopped:'已停止',cancelled:'已取消',archived:'已归档'};
  const state=s=>labels[s]||'状态待核对';
  function result(id,data){
    const box=$(id);if(!box)return;box.classList.add('task-result');box.replaceChildren();
    const s=data.state||data.status||'unknown',badge=n('span',state(s),'task-state');badge.classList.add(Object.hasOwn(labels,s)?s:'unknown');box.append(badge);
    if(['outcome_unknown','needs_review','needs_review_unknown_outcome','unknown'].includes(s)||data.automatic_retry===false){const warning=n('p','结果待确认时，请保留现场并先核对记录，不要直接重复提交。此操作不会自动重试。','evidence-warning');warning.setAttribute('role','alert');box.append(warning);}
    const dl=n('dl');const field=(key,value)=>{dl.append(n('dt',key),n('dd',value));};
    field('工程保存',data.saved===true?'已确认保存':data.saved===false?'未确认保存':'未提供保存结果');
    if(data.error||data.message)field('当前说明',data.error||data.message);
    if(data.id)field('执行编号',data.id);
    if(data.plan_id)field('方案编号',data.plan_id);
    if(s==='verified_unsaved')field('核对结论','已验证：工程尚未保存，未下载 PLC。');
    if(data.assertions_completed!=null)field('仿真检查','已执行 '+data.assertions_completed+' 项 · 未通过 '+(data.failure_count||0)+' 项');
    if(data.verified!=null)field('回读核对',data.verified?'已确认一致':'尚未确认一致');
    if(data.alarm_result)field('报警处理','已返回执行结果，详细证据见下方');
    if(data.text_result)field('文本处理','已返回执行结果，详细证据见下方');
    box.append(dl);
    const items=data.batch?.items||data.import_result?.items||[];
    if(items.length){const wrap=n('div',null,'task-table-wrap'),table=n('table'),head=table.createTHead().insertRow();['对象','执行状态','核对说明'].forEach(x=>head.append(n('th',x)));const tbody=table.createTBody();for(const item of items){const row=tbody.insertRow();[item.block_name||item.name,item.status==='imported'?'已导入':item.status==='importing'?'导入中':item.status||'待核对',item.error||item.message||item.verification||'请查看详细证据'].forEach(x=>row.append(n('td',String(x))));}wrap.append(table);box.append(wrap);}
    if(s==='preview'&&data.configuration){const wrap=n('div',null,'task-table-wrap'),table=n('table');for(const device of data.configuration.devices||[]){for(const alarm of device.alarms||[]){const row=table.insertRow();[device.name,device.instance,'报警 '+alarm.id,alarm.trigger_tag+' · 位 '+alarm.trigger_bit,alarm.text].forEach(v=>row.append(n('td',v)));}}box.append(n('h4','待确认的报警与触发位'));wrap.append(table);box.append(wrap);}
    box.append(n('p',s==='preview'?'下一步：核对全部修改及绑定关系，再勾选确认。':s==='completed'?'下一步：重新读取工程，核对保存后的内容。':s==='stopped'?'后续仿真写入已停止，仿真值保留，请核对现场。':['failed','needs_review','unknown','outcome_unknown'].includes(s)?'下一步：保留现场，查询原任务并核对备份；不要直接重复提交。':'等待当前任务结果，不要重复提交。','workspace-note'));
    const details=n('details',null,'advanced-editor');details.append(n('summary','查看原始结果与诊断证据'),n('pre',JSON.stringify(data,null,2)));box.append(details);
  }
  const watchers=[];let lastScope='';
  function tick(force=false){const key=JSON.stringify(target()),changed=key!==lastScope;lastScope=key;for(const w of watchers){if(force||changed||(!document.hidden&&$(w.id)?.closest('.tab-content')?.classList.contains('active')))w.fn();}}
  window.BridgeUI={node:n,target,state,result,watch:(id,fn)=>{watchers.push({id,fn});fn();},disabled:(id,reason)=>{const el=$(id);if(!el)return;el.title=el.disabled?reason:'';let help=$(id+'Reason');if(!help){help=n('span',null,'disabled-reason');help.id=id+'Reason';el.parentElement.append(help);el.setAttribute('aria-describedby',help.id);}help.textContent=el.disabled?reason:'';help.hidden=!el.disabled;}};
  setInterval(tick,750);document.addEventListener('visibilitychange',()=>{if(!document.hidden)tick(true);});window.addEventListener('bridge-target-change',()=>tick());
  function button(text,fn){const b=n('button',text,'btn');b.type='button';b.onclick=fn;return b;}
  const menu=button('☰ 导航',()=>{document.body.classList.toggle('nav-collapsed');menu.setAttribute('aria-expanded',String(!document.body.classList.contains('nav-collapsed')));});menu.classList.add('workspace-menu');menu.setAttribute('aria-expanded','true');menu.setAttribute('aria-label','展开或收起导航');document.querySelector('header .brand').prepend(menu);
  new ResizeObserver(entries=>{document.documentElement.style.setProperty('--header-height',entries[0].target.getBoundingClientRect().height+'px');}).observe(document.querySelector('header'));
  function tab(id,title){const panel=n('div',null,'tab-content');panel.id='tab-'+id;$('tab-logs').before(panel);const b=n('button',title,'tab-btn');b.dataset.tab=id;b.onclick=()=>window.switchTab(id);document.querySelector('nav.nav-tabs').append(b);return panel;}
  // Separate maintenance from source comparison while retaining the existing controls.
  const maintenance=tab('maintenance','文本与参数');const maintenanceCard=n('section',null,'card');maintenance.append(maintenanceCard);maintenanceCard.append(n('h3','文本回填与参数初始值'));
  const selector=n('select');selector.id='maintenanceSnapshot';selector.setAttribute('aria-label','回填所用源码快照');const refresh=button('读取源码快照列表',()=>$('reviewRefresh').click());maintenanceCard.append(n('p','选择目标源码快照后，导入已校对翻译表，或选择归档参数生成预览。','workspace-note'),selector,refresh);
  const sourcePanel=$('sourceWriteApply').closest('details');sourcePanel.open=true;maintenanceCard.append(sourcePanel);
  const parameterPicker=n('select');parameterPicker.id='parameterPicker';parameterPicker.setAttribute('aria-label','已归档参数');parameterPicker.append(new Option('选择已归档参数',''));$('sourceParameterId').before(parameterPicker);
  const parameterRefresh=button('刷新参数归档',()=>loadTasks(true));parameterPicker.after(parameterRefresh);
  parameterPicker.onchange=()=>{$('sourceParameterId').value=parameterPicker.value;$('sourceParameterId').dispatchEvent(new Event('input'));};
  selector.onchange=()=>{$('reviewAfter').value=selector.value;$('reviewAfter').dispatchEvent(new Event('change'));tick(true);};
  new MutationObserver(()=>{const value=$('reviewAfter').value;selector.replaceChildren(...Array.from($('reviewAfter').options,o=>new Option(o.text,o.value)));selector.value=value;}).observe($('reviewAfter'),{childList:true});
  $('reviewAfter').addEventListener('change',()=>selector.value=$('reviewAfter').value);
  // Keep long pages focused with subviews; hidden sections keep their model state.
  function subviews(panel,parts){const bar=n('div',null,'workspace-subnav');panel.prepend(bar);function show(index){parts.forEach((p,i)=>{p[1].hidden=i!==index;bar.children[i].setAttribute('aria-pressed',String(i===index));});}const add=(title,content)=>{const i=parts.length;parts.push([title,content]);bar.append(button(title,()=>show(i)));content.hidden=true;};const initial=parts.splice(0);initial.forEach(p=>add(...p));panel.addSubview=add;panel.showSubview=content=>show(parts.findIndex(p=>p[1]===content));show(0);}
  subviews($('tab-diff'),[['源码快照与影响',$('reviewCapture').closest('section')],['历史备份差异',document.querySelector('.diff-container')]]);
  const ioCards=Array.from($('tab-io').children).filter(x=>x.classList.contains('card'));if(ioCards.length>=2)subviews($('tab-io'),[['点位与引用',ioCards[0]],['Excel 对账与写回',ioCards[1]]]);
  const stepbar=n('div',null,'step-nav');stepbar.setAttribute('aria-label','机构生成步骤');$('cyStatus').after(stepbar);
  const steps=[['1 选择样板','cyHost'],['2 名称与点位','cyBinding'],['3 核对修改','cyResult'],['4 执行结果','cyApplyPanel']];
  steps.forEach(([title,id],i)=>{const b=button(title,()=>{$(id).scrollIntoView({behavior:'smooth',block:'start'});});stepbar.append(b);window.BridgeUI.watch('cyStatus',()=>{const el=$(id);b.disabled=!el||el.hidden||!!el.closest('[hidden]');b.title=b.disabled?'完成前面的步骤后此区域才会出现':'';b.setAttribute('aria-current',!b.disabled&&i===steps.filter(x=>$(x[1])&&!$(x[1]).hidden&&!$(x[1]).closest('[hidden]')).length-1?'step':'false');});});
  // Persistent tasks from disk, plus the established cylinder recovery history.
  const tasks=tab('tasks','任务中心'),card=n('section',null,'card');tasks.append(card);const slot=n('div');slot.id='tasksExecutionSlot';tasks.append(slot);const heading=n('div',null,'workspace-heading');heading.append(n('h2','任务中心'),button('刷新执行记录',()=>loadTasks(true)));card.append(heading);
  const nav=document.querySelector('nav.nav-tabs');nav.children[0].after(nav.querySelector('[data-tab="tasks"]'));nav.querySelector('[onclick*="diff"]').after(nav.querySelector('[data-tab="maintenance"]'));
  const filter=n('select'),search=n('input');filter.id='taskFilter';[['all','全部记录'],['active','执行中 / 待核对'],['completed','已完成'],['preview','待确认方案']].forEach(([v,t])=>filter.append(new Option(t,v)));search.placeholder='搜索机构、对象或执行编号';search.setAttribute('aria-label','搜索任务');const controls=n('div',null,'audit-actions');controls.append(filter,search);card.append(controls);
  const notice=n('p',null,'workspace-note'),body=n('div',null,'task-table-wrap');notice.setAttribute('role','status');card.append(notice,body);const taskDetail=n('section');taskDetail.id='taskCenterDetail';card.append(taskDetail);
  let records=[],loading=false,serial=0,scope='',stamp=0;
  async function openTask(row){
    if(row.kind==='logic_plan'||row.kind==='download'){window.dispatchEvent(new CustomEvent('bridge-open-record',{detail:row}));return;}
    if(row.kind==='cylinder'){window.switchTab('logs');window.openEngineeringTask?.(row.id);return;}
    if(row.kind==='tag'){window.switchTab('io');const bars=$('tab-io').querySelector('.workspace-subnav');bars?.children[1]?.click();$('tagReviewHistory').click();return;}
    if(row.kind==='parameter'){window.switchTab('maintenance');$('sourceParameterId').value=row.id;$('sourceParameterId').dispatchEvent(new Event('input'));return;}
    if(row.kind==='source'){window.switchTab('maintenance');$('sourceWriteJobId').value=row.id;$('sourceWriteJobId').dispatchEvent(new Event('input'));await new Promise(r=>setTimeout(r,0));$('sourceWriteCheck').click();return;}
    if(row.kind==='hmi'){window.switchTab('cylinder');$('hmiCompanionName').closest('details').open=true;$('hmiCompanionName').value=row.hmi_name;$('hmiCompanionName').dispatchEvent(new Event('input'));$('hmiCompanionJob').value=row.id;$('hmiCompanionJob').dispatchEvent(new Event('input'));$('hmiCompanionCheck').click();return;}
    if(row.kind==='peripheral'){const t=target();try{const response=await fetch('/api/io/peripheral/status',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...t,instance_name:row.instance_name,run_id:row.id})});const data=await response.json();if(JSON.stringify(t)!==JSON.stringify(target()))return;if(!response.ok)throw Error(data.error);result(taskDetail.id,data);}catch(e){notice.textContent=e.message;} }
  }
  function renderTasks(){body.replaceChildren();const table=n('table'),head=table.createTHead().insertRow();['最近更新','任务','状态','工程保存','操作'].forEach(x=>head.append(n('th',x)));const q=search.value.toLowerCase();const filtered=records.filter(r=>(filter.value==='all'||filter.value==='active'&&!['completed','verified_unsaved','preflight_ready','preview','archived','stopped','cancelled'].includes(r.state)||filter.value==='completed'&&['completed','archived','verified_unsaved','preflight_ready'].includes(r.state)||filter.value===r.state)&&[r.title,r.id,r.message].join(' ').toLowerCase().includes(q));
    const tbody=table.createTBody();for(const r of filtered){const tr=tbody.insertRow();tr.append(n('td',r.updated?new Date(r.updated).toLocaleString():'时间未知'),n('td',r.title,'task-row-title'),n('td',state(r.state)),n('td',r.saved===true?'已保存':r.saved===false?'未确认保存':'—'));const cell=n('td');cell.append(button('打开记录',()=>openTask(r)));tr.append(cell);}body.append(table);if(!filtered.length)body.append(n('p','当前筛选没有记录。'));
  }
  filter.onchange=search.oninput=renderTasks;
  async function loadTasks(force=false){const t=target(),key=JSON.stringify(t);if(loading||!t.target_token||!force&&Date.now()-stamp<5000)return;loading=true;const ticket=++serial;try{const query=new URLSearchParams(t);const responses=await Promise.all([fetch('/api/io/tasks?'+query,{cache:'no-store'}),fetch('/api/io/cylinder-history?'+query+'&limit=100',{cache:'no-store'})]);const data=await Promise.all(responses.map(r=>r.json()));if(ticket!==serial||key!==JSON.stringify(target()))return;if(!responses[0].ok)throw Error(data[0].error||'任务记录读取失败');records=[...(data[0].jobs||[]),...(responses[1].ok?data[1].jobs||[]:[]).map(r=>({...r,kind:'cylinder',title:'机构生成 · '+(r.cylinder_name||r.instance_name||r.id)}))].sort((a,b)=>String(b.updated||'').localeCompare(String(a.updated||'')));notice.textContent='当前工程近期 '+records.length+' 条记录 · '+new Date().toLocaleTimeString()+' 更新。'+(data[0].warnings||[]).join(' ')+(responses[1].ok?'':' 机构记录暂未读取成功。');renderTasks();const selected=parameterPicker.value;parameterPicker.replaceChildren(new Option('选择已归档参数',''),...records.filter(r=>r.kind==='parameter').map(r=>new Option(new Date(r.updated).toLocaleString()+' · '+r.id.slice(0,8),r.id)));parameterPicker.value=selected;stamp=Date.now();}catch(e){notice.textContent='读取失败：'+e.message+'。保留上次列表，不会重放任务。';}finally{loading=false;}}
  window.BridgeUI.watch('taskFilter',()=>{const key=JSON.stringify(target());if(scope!==key){scope=key;serial++;records=[];stamp=0;body.replaceChildren();taskDetail.replaceChildren();notice.textContent='选择工程与 PLC 后读取记录。';parameterPicker.replaceChildren(new Option('选择已归档参数',''));}if(tasks.classList.contains('active'))loadTasks();});
  window.addEventListener('bridge-tab-change',e=>{tick(true);if(e.detail==='tasks'||e.detail==='maintenance')loadTasks(true);});
  const header=n('div',null,'source-badge');header.id='workspaceConnection';header.setAttribute('role','status');document.querySelector('main').prepend(header);let lastConnected=0;
  window.addEventListener('bridge-status',e=>{if(e.detail.ok)lastConnected=Date.now();header.textContent=e.detail.ok?'已连接看板 · '+(target().project_path.split(/[\\/]/).pop()||'未选择工程')+' · '+(target().plc_name||'未选择 PLC')+' · 状态更新于 '+new Date().toLocaleTimeString()+'。工程源码需在对应页面重新读取。':'看板连接中断 · 最后成功更新 '+(lastConnected?new Date(lastConnected).toLocaleTimeString():'未知')+'。当前显示可能过期，请重新打开看板启动程序；不要重复提交写入。';});header.textContent='正在连接看板…';
  const reasons={sourceWriteApply:'先选择源码快照并生成完整预览，再勾选确认；直接写回支持 V16–V20。',hmiCompanionApply:'先读取 HMI 并核对预览，再勾选确认；直接写回限 V17。',monitorConnect:'先选择工程和 PLC；已有连接时需先断开。',peripheralStart:'选择 V17 工程、填写受管实例并勾选确认；已有任务时先核对原任务。',tagReviewApply:'先勾选对账差异并生成预览，再确认保存工程。'};
  for(const [id,reason] of Object.entries(reasons))window.BridgeUI.watch(id,()=>window.BridgeUI.disabled(id,reason));
})();
