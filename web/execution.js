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
      cell(t.message || '—');
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
