(() => {
  const rows = document.getElementById('executionRows');
  const summary = document.getElementById('executionSummary');
  const filter = document.getElementById('executionFilter');
  const names = { trust: '注册白名单', 'build-index': '建立工程索引', export: '导出源码', import: '导入源码', compile: '编译 PLC', 'io-read': '读取 I/O 表',
    tia_compile_plc: '编译 PLC', tia_read_block: '读取程序块', tia_read_source: '读取源码', tia_list_blocks: '列出程序块', tia_build_project_index: '建立工程索引' };
  const labels = { running: '执行中', completed: '成功', failed: '失败', cancelled: '已取消', waiting: '等待后台结果', warning: '有警告', unknown: '结果待确认' };
  const colors = { running: 'badge-amber', waiting: 'badge-amber', failed: 'badge-red', completed: 'badge-green', warning: 'badge-amber', unknown: 'badge-amber' };
  let tasks = [], pending = false, previous = '', lastContact = 0, error = '';
  const active = task => task.status === 'running' || task.status === 'waiting';
  function render() {
    const count = status => tasks.filter(t => t.status === status).length;
    const working = tasks.filter(active).length;
    const stamp = lastContact ? new Date(lastContact).toLocaleTimeString() : '—';
    summary.textContent = (error ? '日志连接中断，保留上次结果 · ' : '') + '执行中 ' + working + ' · 成功 ' + count('completed') +
      ' · 失败 ' + count('failed') + ' · 其他 ' + (tasks.length - working - count('completed') - count('failed')) +
      ' · 更新于 ' + stamp + ' · 显示进行中任务及最近 100 条结果';
    const selected = tasks.filter(t => filter.value === 'all' || (filter.value === 'running' ? active(t) :
      filter.value === 'attention' ? ['warning','cancelled','unknown','waiting'].includes(t.status) : t.status === filter.value));
    selected.sort((a,b) => Number(active(b)) - Number(active(a)) || b.updated.localeCompare(a.updated));
    const fragment = document.createDocumentFragment();
    for (const t of selected) {
      const row = document.createElement('tr');
      const cell = value => { const td = document.createElement('td'); td.textContent = value; row.appendChild(td); return td; };
      cell(new Date(t.started).toLocaleString());
      const badge = document.createElement('span');
      badge.className = 'badge ' + (colors[t.status] || ''); badge.textContent = labels[t.status] || t.status;
      cell('').appendChild(badge);
      cell(t.source + ' · ' + (names[t.action] || t.action)).title = t.action;
      cell(t.target || '—');
      const duration = Math.max(0, (active(t) ? lastContact : Date.parse(t.updated)) - Date.parse(t.started));
      cell(duration < 1000 ? duration + ' ms' : (duration / 1000).toFixed(1) + ' 秒');
      const messageCell = cell(t.message || '—');
      if (t.operation_id) {
        const link = document.createElement('button'); link.type = 'button'; link.className = 'btn btn-sm';
        link.textContent = '查看操作详情'; link.style.marginLeft = '8px';
        link.onclick = () => window.openEngineeringTask && window.openEngineeringTask(t.operation_id);
        messageCell.appendChild(link);
      }
      fragment.appendChild(row);
    }
    if (!selected.length) {
      const row = document.createElement('tr'), td = document.createElement('td');
      td.colSpan = 6; td.textContent = error && !lastContact ? '暂时无法读取日志' : '当前没有符合条件的任务'; row.appendChild(td); fragment.appendChild(row);
    }
    rows.replaceChildren(fragment);
  }
  async function refresh() {
    if (pending || document.hidden || !rows.closest('.tab-content').classList.contains('active')) return;
    pending = true;
    try {
      const response = await fetch('/api/execution-log', {cache:'no-store', signal: AbortSignal.timeout(5000)});
      if (!response.ok) throw new Error('HTTP ' + response.status);
      const data = await response.json();
      if (!Array.isArray(data.tasks)) throw new Error('日志格式错误');
      const serialized = JSON.stringify(data.tasks);
      const changed = error || previous !== serialized || data.tasks.some(active);
      tasks = data.tasks; lastContact = Date.now(); error = ''; previous = serialized;
      if (changed) render();
    } catch (ex) { error = ex.message; render(); }
    finally { pending = false; }
  }
  filter.addEventListener('change', render);
  window.showExecutionLog = tab => {
    if (tab !== 'dashboard' && tab !== 'logs') return;
    document.getElementById(tab === 'dashboard' ? 'dashboardExecutionSlot' : 'logsExecutionSlot')
      .appendChild(document.getElementById('executionJournal'));
    refresh();
  };
  document.addEventListener('visibilitychange', refresh);
  refresh(); setInterval(refresh, 1000);
})();

/* Durable engineering history is separate from the rotating diagnostic log. */
(() => {
  const el = id => document.getElementById(id), root = el('engineeringHistory');
  if (!root) return;
  const selectedKey = 'tia-cylinder-history-selected-v1', pendingKey = 'tia-cylinder-history-pending-v1';
  const labels = {running:'执行中', waiting:'等待后台结果', completed:'完成', failed:'失败', needs_review:'需要核对', outcome_unknown:'结果待确认'};
  const stages = {preflight:'读取与检查', validate:'生成方案', backup:'备份', write:'写入、编译与回读', import:'写入', compile:'编译', verify:'回读核对', save:'保存', waiting:'等待后台结果', interrupted:'结果待确认', finished:'任务结束', check:'核对工程', checked:'核对完成', evidence:'记录执行后状态'};
  const states = {pending:'未执行', validated:'检查通过', backed_up:'已备份', importing:'正在写入', imported:'已写入', outcome_unknown:'写入结果待确认', failed:'失败', matched:'回读一致', different:'回读有差异', not_run:'尚未核对', verified:'核对通过', before:'仍为操作前内容', planned:'与计划一致', changed:'发现其他修改', conflict:'发现其他修改', not_written:'尚未写入', associated:'关联实例 DB', unchanged:'未重复写入'};
  const terminal = job => ['completed','failed','needs_review','outcome_unknown'].includes(job.state);
  let offset = 0, total = 0, listBusy = false, selected = '', job = null, detailVersion = 0, detailBusy = false;
  let pending = null, pendingPolling = false, planChoice = '', detailJson = '', listStamp = 0;
  const limit = 30;
  const text = (tag, value, className) => { const node = document.createElement(tag); node.textContent = value == null ? '' : String(value); if(className) node.className = className; return node; };
  const stamp = value => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : '—';
  const target = value => [value.project || value.project_path, value.plc || value.plc_name].filter(Boolean).join(' · ');
  const batch = value => value.result?.import_result || value.result?.import || value.result?.batch || value.result?.live_batch || {};
  const saved = value => value.result?.saved === true || value.saved === true;
  const warningCount = value => batch(value).warning_count ?? value.warning_count;
  const actionName = value => value.recovery_action === 'check' ? '核对工程' : value.recovery_action === 'continue' ? '继续处理' : value.recovery_action === 'restore' ? '恢复备份' : '生成气缸';
  function resultLabel(value) {
    if(value.stage === 'interrupted' || value.result?.outcome_unknown === true) return '结果待确认';
    if(value.state === 'completed' && value.recovery_action === 'check') return '核对完成';
    if(value.state === 'completed' && saved(value)) return '已保存' + (Number(warningCount(value)) > 0 ? ' · ' + warningCount(value) + ' 条警告' : '');
    if(value.state === 'needs_review' && batch(value).ok === true && !saved(value)) return '写入完成 · 保存未确认';
    if(value.state === 'needs_review') return '需要核对 · 保存未确认';
    if(value.state === 'running' && value.stage === 'waiting') return '等待后台结果';
    return labels[value.state] || value.state || '结果待确认';
  }
  function button(label, fn, disabled = false, primary = false) {
    const b = text('button', label, 'btn btn-sm' + (primary ? ' btn-primary' : '')); b.type = 'button'; b.disabled = disabled; b.onclick = fn; return b;
  }
  function table(headings, rows) {
    const wrapper = document.createElement('div'); wrapper.style.overflowX = 'auto';
    const t = document.createElement('table'); t.className = 'execution-table engineering-detail-table';
    const head = t.createTHead().insertRow(); headings.forEach(h => head.appendChild(text('th', h)));
    rows.forEach(values => { const row = t.insertRow(); values.forEach(value => row.appendChild(text('td', value))); });
    wrapper.appendChild(t); return wrapper;
  }
  function section(parent, title, body, open = false, key = title) {
    const details = document.createElement('details'); details.dataset.section = key; details.open = open;
    details.appendChild(text('summary', title)); details.appendChild(body); parent.appendChild(details); return details;
  }
  function rememberPending() {
    try { if(pending) localStorage.setItem(pendingKey,JSON.stringify(pending)); else localStorage.removeItem(pendingKey); } catch(_) {}
    window.cylinderRecoveryPending = !!pending;
    window.dispatchEvent(new CustomEvent('cylinder-recovery-pending', {detail:{pending:!!pending}}));
  }
  function notice(message) { el('engineeringTaskDetail').hidden = false; el('engineeringTaskNotice').textContent = message || ''; }
  async function request(url, options = {}) {
    const response = await fetch(url, {cache:'no-store', signal:AbortSignal.timeout(10000), ...options});
    let data;
    try { data = await response.json(); } catch(_) { throw new Error('未收到完整的任务结果'); }
    return {response, data};
  }
  async function refreshHistory(force = false) {
    if(listBusy || document.hidden || (!force && !root.closest('.tab-content')?.classList.contains('active'))) return;
    listBusy = true;
    try {
      const {response,data} = await request('/api/io/cylinder-history?' + new URLSearchParams({offset,limit}));
      if(!response.ok || !Array.isArray(data.jobs)) throw new Error(data.error || '无法读取工程变更记录');
      total = Number(data.total) || 0;
      if(total && offset >= total) {offset = Math.floor((total-1)/limit)*limit; listBusy=false; return refreshHistory(true);}
      const body = el('engineeringHistoryRows'), fragment = document.createDocumentFragment();
      data.jobs.forEach(value => {
        const row = document.createElement('tr');
        [stamp(value.started || value.updated),actionName(value) + ' · ' + [value.cylinder_name,value.instance_name].filter(Boolean).join(' / '),target(value),resultLabel(value)].forEach(v => row.appendChild(text('td',v)));
        const cell = document.createElement('td'); cell.appendChild(button('查看', () => openJob(value.id))); row.appendChild(cell); fragment.appendChild(row);
      });
      if(!data.jobs.length) { const row=document.createElement('tr'),cell=text('td','暂无保存的工程变更记录');cell.colSpan=5;row.appendChild(cell);fragment.appendChild(row); }
      body.replaceChildren(fragment); listStamp = Date.now();
      const warnings=Array.isArray(data.warnings)?data.warnings:[];
      el('engineeringHistorySummary').textContent = '共 ' + total + ' 条记录 · 包含原任务与后续处理 · 更新于 ' + new Date(listStamp).toLocaleTimeString() + (warnings.length?' · '+warnings.length+' 条记录读取异常：'+warnings.join('；'):'');
      el('engineeringHistoryPage').textContent = total ? (offset+1) + '–' + Math.min(offset+limit,total) + ' / ' + total : '0 条';
      el('engineeringHistoryPrev').disabled = offset === 0; el('engineeringHistoryNext').disabled = offset+limit >= total;
    } catch(error) { el('engineeringHistorySummary').textContent = '记录暂时无法刷新，保留上次结果：' + error.message; }
    finally {listBusy=false;}
  }
  function recoveryReport(value) { return value.result?.recovery_check || value.result?.latest_check || null; }
  function renderDetail(value) {
    const body = el('engineeringTaskBody');
    const opened = new Set([...body.querySelectorAll('details[open]')].map(d=>d.dataset.section));
    body.replaceChildren();
    el('engineeringTaskTitle').textContent = actionName(value) + ' · ' + ([value.cylinder_name,value.instance_name].filter(Boolean).join(' / ') || '操作详情');
    body.appendChild(text('p',target(value),'engineering-target'));
    body.appendChild(text('p',resultLabel(value),'engineering-result'));
    body.appendChild(text('p',[value.message,value.error].filter((s,i,a)=>s&&a.indexOf(s)===i).join('\n'),'engineering-message'));
    if(value.result?.evidence_error)body.appendChild(text('p',value.result.evidence_error,'engineering-message'));
    body.appendChild(text('p','开始：' + stamp(value.started) + '　更新：' + stamp(value.updated) + '　操作编号：' + value.id,'engineering-meta'));
    const links = document.createElement('div'); links.className = 'engineering-actions';
    if(value.parent_id) links.appendChild(button('查看原任务',()=>openJob(value.parent_id)));
    if(value.resolution_id) links.appendChild(button('查看后续处理结果',()=>openJob(value.resolution_id)));
    else if(value.result?.latest_recovery_id) links.appendChild(button('查看最近处理记录',()=>openJob(value.result.latest_recovery_id)));
    if(value.result?.consumed_by) links.appendChild(button('查看使用此核对的处理任务',()=>openJob(value.result.consumed_by)));
    if(links.children.length) body.appendChild(links);
    const events = Array.isArray(value.events) ? value.events : [];
    if(events.length) section(body,'执行过程',table(['时间','步骤','说明'],events.map(e=>[stamp(e.at),stages[e.stage]||e.stage,e.message])),opened.has('events'),'events');
    const imported = batch(value), items = Array.isArray(imported.items) ? imported.items : [];
    if(items.length) section(body,'程序块与备份',table(['程序块','写入与核对','备份位置'],items.map(i=>[i.block_name,[states[i.status]||i.status,states[i.verification]||i.verification,i.error].filter(Boolean).join(' · '),i.backup||'尚未返回备份位置'])),true,'blocks');
    const instances = value.result?.instance_db_backups;
    if(Array.isArray(instances) && instances.length) section(body,'关联实例 DB 备份',table(['程序块','备份位置'],instances.map(i=>[i.block_name,i.path])),opened.has('instances'),'instances');
    const recoveryBackups=value.result?.recovery_backups;
    if(Array.isArray(recoveryBackups) && recoveryBackups.length) section(body,'后续处理前的现场备份',table(['程序块','备份位置'],recoveryBackups.map(i=>[i.block_name,i.path])),opened.has('recovery-backups'),'recovery-backups');
    if(imported.error_count != null || imported.warning_count != null || imported.compile_report) {
      const report=text('pre',imported.compile_report || '未返回详细编译报告','engineering-report');
      section(body,'编译结果：' + (imported.error_count ?? '未确认') + ' 个错误，' + (imported.warning_count ?? '未确认') + ' 条警告',report,opened.has('compile'),'compile');
    }
    const check = recoveryReport(value), actions = document.createElement('div'); actions.className = 'engineering-actions';
    if(!value.resolution_id && value.recovery_action !== 'check' && value.state === 'needs_review' && value.can_check !== false) {
      actions.appendChild(button('核对当前工程',()=>submit('check',value.id),!!pending || !!window.cylinderApplying,true));
      body.appendChild(text('p','先读取当前工程、核对后台状态与备份；核对不会写入或保存工程。','engineering-meta'));
    }
    if(value.state === 'outcome_unknown' || value.stage === 'interrupted' || (value.state === 'running' && value.stage === 'waiting')) {
      body.appendChild(text('p','执行结果尚未确认。查询状态不会再次提交写入。','engineering-meta'));
      actions.appendChild(button('重新查询任务状态',()=>openJob(value.id)));
    }
    if(check) {
      const checkBody=document.createElement('div');
      checkBody.appendChild(text('p',check.reason || '请查看逐块核对结果。','engineering-message'));
      if(Array.isArray(check.items)) checkBody.appendChild(table(['程序块','核对结果','说明'],check.items.map(i=>[i.block_name,states[i.status]||i.status,i.message])));
      section(body,'当前工程核对结果',checkBody,true,'check');
      const available=!value.resolution_id && !value.result?.consumed_by;
      const canContinue = available && check.can_continue === true, canRestore = available && check.can_restore === true;
      if(!available)body.appendChild(text('p','这份核对已有后续处理记录，请查看关联任务；不能再次使用同一份核对提交写入。','engineering-meta'));
      if(canContinue || canRestore) {
        actions.appendChild(button('查看继续方案',()=>{planChoice='continue';renderDetail(job);},!canContinue||!!pending));
        actions.appendChild(button('查看恢复方案',()=>{planChoice='restore';renderDetail(job);},!canRestore||!!pending));
      }
      if(planChoice && (planChoice === 'continue' ? canContinue : canRestore)) {
        const plan=document.createElement('div'); plan.className='engineering-plan';
        plan.appendChild(text('h4',planChoice === 'restore' ? '恢复本次变更' : '继续完成剩余步骤'));
        const steps=planChoice === 'restore' ? check.restore_steps : check.continue_steps, list=document.createElement('ol');
        (Array.isArray(steps)?steps:[]).forEach(s=>list.appendChild(text('li',s))); plan.appendChild(list);
        plan.appendChild(text('p','执行前会再次核对，发现后续修改时停止。检查通过后将保存当前整个博途工程，其中也会包含其他未保存的修改。','engineering-message'));
        const controls=document.createElement('div');controls.className='engineering-actions';
        controls.appendChild(button(planChoice === 'restore' ? '确认恢复并保存工程' : '确认继续并保存工程',()=>submit(planChoice,check.source_id || value.parent_id || value.id,check.check_id || value.id),!!pending,true));
        controls.appendChild(button('取消',()=>{planChoice='';renderDetail(job);},!!pending));plan.appendChild(controls);body.appendChild(plan);
      }
    }
    if(actions.children.length) body.appendChild(actions);
    if(value.state === 'needs_review' && value.can_check === false && !check) body.appendChild(text('p',value.recovery_reason || '这条记录缺少完整的恢复证据，请核对工程及已有备份后处理。','engineering-meta'));
    if(value.result) section(body,'完整执行报告',text('pre',JSON.stringify(value.result,null,2),'engineering-report'),opened.has('raw'),'raw');
  }
  async function loadDetail(id, version) {
    const {response,data} = await request('/api/io/cylinder-apply-status?' + new URLSearchParams({id}));
    if(version !== detailVersion || selected !== id) return;
    if(!response.ok) throw new Error(data.error || '无法读取这条操作记录');
    const serialized=JSON.stringify(data);
    if(serialized !== detailJson) {
      const oldCheck=recoveryReport(job || {})?.check_id, newCheck=recoveryReport(data)?.check_id;
      if(oldCheck !== newCheck) planChoice='';
      job=data;detailJson=serialized;renderDetail(data);
    }
    if(!pending) notice('');
  }
  async function openJob(id, show = true) {
    if(!id) return;
    const version=++detailVersion;selected=id;job=null;detailJson='';planChoice='';
    try {localStorage.setItem(selectedKey,id);}catch(_){}
    notice('正在读取操作记录…');el('engineeringTaskBody').replaceChildren();
    if(show && !root.closest('.tab-content')?.classList.contains('active') && typeof window.switchTab === 'function') window.switchTab('dashboard');
    try {await loadDetail(id,version);}catch(error){if(version===detailVersion)notice(error.message);}
    if(show) el('engineeringTaskDetail').scrollIntoView({behavior:'smooth',block:'nearest'});
  }
  async function pollPending() {
    if(pendingPolling || !pending) return;pendingPolling=true;
    try {
      while(pending) {
        try {
          const query=pending.id?{id:pending.id}:{request_id:pending.request_id};
          const {response,data}=await request('/api/io/cylinder-apply-status?' + new URLSearchParams(query));
          if(!response.ok) throw new Error(data.error || (response.status===404?'尚未找到已提交任务':'无法读取任务状态'));
          if(!data.id) throw new Error('任务结果缺少操作编号');
          if(!pending.id){pending.id=data.id;rememberPending();}
          if(selected !== data.id) {selected=data.id;detailVersion++;planChoice='';try{localStorage.setItem(selectedKey,data.id);}catch(_){} }
          job=data;detailJson=JSON.stringify(data);renderDetail(data);notice(data.message || '正在执行…');
          if(terminal(data)) {
            const writes=pending.action !== 'check';pending=null;rememberPending();planChoice='';renderDetail(data);notice('');
            if(writes && window.cylinderIoInvalidate)window.cylinderIoInvalidate();
            refreshHistory(true);break;
          }
        }catch(error){notice('提交结果尚未确认，正在查询同一次任务，不会重复提交。\n' + error.message);}
        await new Promise(resolve=>setTimeout(resolve,1500));
      }
    } finally {pendingPolling=false;}
  }
  async function submit(action, sourceId, checkId) {
    if(pending || window.cylinderApplying) return;
    const requestId=crypto.randomUUID();
    const payload=action === 'check'?{id:sourceId,request_id:requestId}:{id:sourceId,check_id:checkId,request_id:requestId,action,confirm:true,confirm_save:true};
    pending={request_id:requestId,id:'',source_id:sourceId,action};rememberPending();
    if(job)renderDetail(job);notice(action === 'check'?'正在提交只读核对…':'正在提交已确认的处理方案…');
    try {
      const {response,data}=await request('/api/io/' + (action==='check'?'cylinder-recovery-check':'cylinder-recover'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
      if(!response.ok && response.status<500 && !data.id) {pending=null;rememberPending();if(job)renderDetail(job);notice(data.error || '请求未被接受；原任务详情已保留。');return;}
      if(!response.ok && !data.id) throw new Error(data.error || '尚未收到任务提交结果');
      if(!data.id) throw new Error('尚未收到操作编号');
      pending.id=data.id;rememberPending();
    }catch(error){notice('提交结果尚未确认，正在查询同一次任务，不会重复提交。\n' + error.message);}
    await pollPending();
  }
  window.openEngineeringTask=openJob;
  window.refreshEngineeringHistory=()=>refreshHistory(true);
  el('engineeringHistoryRefresh').onclick=()=>refreshHistory(true);
  el('engineeringHistoryPrev').onclick=()=>{offset=Math.max(0,offset-limit);refreshHistory(true);};
  el('engineeringHistoryNext').onclick=()=>{if(offset+limit<total){offset+=limit;refreshHistory(true);}};
  el('engineeringTaskClose').onclick=()=>{selected='';job=null;detailVersion++;el('engineeringTaskDetail').hidden=true;try{localStorage.removeItem(selectedKey);}catch(_){};};
  window.addEventListener('cylinder-job-finished',event=>{refreshHistory(true);if(event.detail?.id)openJob(event.detail.id,false);});
  try { const remembered=JSON.parse(localStorage.getItem(pendingKey));if(remembered && /^[a-zA-Z0-9-]{16,64}$/.test(remembered.request_id || ''))pending=remembered; }catch(_){}
  if(pending){rememberPending();notice('正在恢复上次处理任务的进度…');pollPending();}
  else {try{const id=localStorage.getItem(selectedKey);if(id)openJob(id,false);}catch(_){} }
  refreshHistory();
  setInterval(async()=>{
    if(document.hidden || !root.closest('.tab-content')?.classList.contains('active'))return;
    if(Date.now()-listStamp>5000)refreshHistory();
    if(!pending && selected && job && !terminal(job) && !detailBusy){detailBusy=true;try{await loadDetail(selected,detailVersion);}catch(error){notice('进度暂时无法刷新，保留上次结果：'+error.message);}finally{detailBusy=false;}}
  },1500);
})();
