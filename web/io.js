/* I/O snapshots are bound to the project/PLC selected when a read starts. */
(() => {
  const state = { id: '', target: null, busy: false, ready: false, page: 1, pages: 1, sequence: 0, refSequence: 0 };
  const el = id => document.getElementById(id);
  const text = (id, value) => { el(id).textContent = value || ''; };
  const normalize = s => (s || '').replace(/\\/g, '/').toLowerCase();
  const target = () => ({ project_path: currentStatus.active_project || currentStatus.project_path || '', plc_name: currentStatus.target_plc || '', target_token: currentStatus.target_token || '' });
  const same = (a, b) => !!a && !!b && normalize(a.project_path) === normalize(b.project_path) && a.plc_name === b.plc_name;
  const current = () => same(state.target, target());
  const message = value => text('ioMessage', value);
  const cell = value => { const td = document.createElement('td'); td.textContent = value == null ? '' : String(value); td.style.cssText = 'padding:10px;border-bottom:1px solid #334155;white-space:pre-wrap;word-break:break-word;'; return td; };
  const empty = (id, value) => { const tr = document.createElement('tr'); const td = cell(value); td.colSpan = 6; tr.appendChild(td); el(id).replaceChildren(tr); };
  const filters = () => ({ area: el('ioArea').value, q: el('ioSearch').value, table: el('ioTableFilter').value });
  const url = (path, extra = {}) => '/api/io/' + path + '?' + new URLSearchParams({ id: state.id, ...filters(), ...extra });
  async function json(path, options) {
    const response = await fetch(path, { cache: 'no-store', ...options });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || '请求失败：' + response.status);
    return data;
  }
  function controls() {
    const t = target();
    el('ioReadButton').disabled = state.busy || !t.target_token || !t.plc_name;
    el('ioReadButton').textContent = state.busy ? '读取中…' : '🔄 重新读取';
    el('ioExportButton').disabled = state.busy || !state.ready || !current();
    el('ioPrev').disabled = !state.ready || !current() || state.page <= 1;
    el('ioNext').disabled = !state.ready || !current() || state.page >= state.pages;
  }
  window.ioTargetChanged = () => {
    const t = target(); text('ioTarget', (t.project_path || '未选择工程') + '  ·  ' + (t.plc_name || '未选择 PLC'));
    if (state.target && !current()) {
      empty('ioRows', '工程或 PLC 已切换，请重新读取。'); text('ioSummary', '上一份读取结果属于其它目标，已停止显示。');
      el('ioReferencesPanel').hidden = true; el('ioWarningsBox').hidden = true;
      state.ready = false; state.sequence++; state.refSequence++;
      message(state.busy ? '正在完成上一目标的读取；完成后可读取新目标。' : '请选择“重新读取”，获取当前目标的数据。');
    }
    controls();
  };
  window.ioRead = async () => {
    if (state.busy) return;
    const selected = target();
    if (!selected.target_token || !selected.plc_name) { message('请先选择工程与 PLC，等待目标识别完成。'); return; }
    state.busy = true; state.ready = false; state.id = ''; state.target = selected; state.page = 1; state.sequence++; state.refSequence++;
    el('ioReferencesPanel').hidden = true; el('ioWarningsBox').hidden = true; text('ioSummary', '');
    empty('ioRows', '正在读取新的数据快照…'); message('正在启动读取…'); controls();
    try {
      const started = await json('/api/io/read', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(selected) });
      state.id = started.id;
      while (true) {
        const status = await json(url('status'));
        if (current()) message(status.message);
        if (status.state === 'failed') throw new Error(status.error || '读取失败');
        if (status.state === 'completed') break;
        await new Promise(resolve => setTimeout(resolve, 1000));
      }
      if (current()) { el('ioTableFilter').replaceChildren(new Option('全部变量表', '')); await load(true); }
      else message('上一目标读取已完成。请重新读取当前工程 / PLC。');
    } catch (error) { message('读取失败：' + error.message); }
    finally { state.busy = false; controls(); }
  };
  async function load(resetTables = false) {
    if (!state.id || !current()) return;
    const sequence = ++state.sequence, id = state.id;
    try {
      const data = await json(url('data', { page: state.page }));
      if (sequence !== state.sequence || id !== state.id || !current()) return;
      state.ready = true; state.page = data.page; state.pages = Math.max(1, Math.ceil(data.total / data.page_size));
      if (resetTables) data.tables.forEach(table => el('ioTableFilter').add(new Option(table, table)));
      text('ioSummary', '读取完成：' + new Date(data.read_completed_utc).toLocaleString() + '  ·  变量表 ' + data.tables_read + '/' + data.tables_total + '  ·  当前筛选 ' + data.total + ' / 全部 ' + data.total_all + ' 项\n' + data.reference_source + '：' + data.reference_coverage);
      el('ioWarningsBox').hidden = data.warnings.length === 0; text('ioWarnings', data.warnings.join('\n'));
      text('ioPageLabel', '第 ' + state.page + ' / ' + state.pages + ' 页 · 每页 100 项');
      const fragment = document.createDocumentFragment();
      data.rows.forEach(row => {
        const tr = document.createElement('tr');
        [row.name, row.address || (row.area === 'CONST' ? '常量 = ' + row.value : '—'), row.data_type, row.comment, [row.table, row.group, row.unit].filter(Boolean).join(' / ')].forEach(v => tr.appendChild(cell(v)));
        const td = cell(''), button = document.createElement('button'); button.className = 'btn btn-sm'; button.textContent = row.reference_count + ' 条证据';
        button.addEventListener('click', () => references(row.id)); td.appendChild(button); tr.appendChild(td); fragment.appendChild(tr);
      });
      el('ioRows').replaceChildren(fragment);
      if (!data.rows.length) empty('ioRows', '当前筛选范围没有变量。可选择“全部变量和常量”或查看读取警告。');
      controls();
    } catch (error) { if (sequence === state.sequence) { state.ready = false; message(error.message); controls(); } }
  }
  window.ioFilter = () => { state.page = 1; state.refSequence++; el('ioReferencesPanel').hidden = true; load(); };
  let searchTimer;
  window.ioSearchChanged = () => { clearTimeout(searchTimer); searchTimer = setTimeout(window.ioFilter, 300); };
  window.ioPage = delta => { state.page = Math.max(1, Math.min(state.pages, state.page + delta)); load(); };
  async function references(row) {
    const sequence = ++state.refSequence, id = state.id;
    try {
      const data = await json(url('references', { row }));
      if (id !== state.id || !current() || sequence !== state.refSequence) return;
      el('ioReferencesPanel').hidden = false; text('ioReferencesTitle', data.name + '  ' + data.address + ' — ' + data.total + ' 条引用证据');
      text('ioReferencesNote', data.coverage);
      const fragment = document.createDocumentFragment();
      data.references.forEach(r => { const tr = document.createElement('tr'); [r.block, r.group, r.access, r.location, r.evidence, r.source + ' / ' + r.state].forEach(v => tr.appendChild(cell(v))); fragment.appendChild(tr); });
      el('ioReferencesRows').replaceChildren(fragment);
      if (!data.total) empty('ioReferencesRows', '当前读取范围内未匹配到引用；不代表该变量未使用。');
      el('ioReferencesPanel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    } catch (error) { message('引用读取失败：' + error.message); }
  }
  window.ioExport = async () => {
    if (!state.ready || !current()) return;
    const id = state.id, selected = state.target;
    el('ioExportButton').disabled = true;
    try {
      const response = await fetch(url('export'), { cache: 'no-store' });
      if (!response.ok) { const data = await response.json(); throw new Error(data.error || '导出失败'); }
      const blob = await response.blob();
      if (id !== state.id || !same(selected, target())) throw new Error('导出期间目标已切换，请重新读取后导出。');
      const objectUrl = URL.createObjectURL(blob), a = document.createElement('a');
      a.href = objectUrl; a.download = 'TIA-IO-' + new Date().toISOString().slice(0, 10) + '.xlsx'; a.click(); setTimeout(() => URL.revokeObjectURL(objectUrl), 10000);
      message('Excel 已导出，包含当前筛选的全部分页、引用明细和读取说明。');
    } catch (error) { message('导出失败：' + error.message); }
    finally { controls(); }
  };
  ioTargetChanged();
})();
