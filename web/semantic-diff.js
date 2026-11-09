(function () {
  'use strict';
  let sequence = 0, listSequence = 0, targetKey = '', result = null, mode = 'semantic';
  const $ = id => document.getElementById(id);
  const node = (tag, text, cls) => { const el = document.createElement(tag); el.textContent = text == null ? '' : String(text); if (cls) el.className = cls; return el; };
  function target() {
    const status = typeof currentStatus === 'undefined' ? {} : currentStatus;
    return { project_path: status.active_project || status.project_path || '', plc_name: status.target_plc || '', target_token: status.target_token || '' };
  }
  function key() { const t = target(); return JSON.stringify([t.project_path, t.plc_name, t.target_token]); }
  function clear(message) {
    sequence++; result = null;
    $('diffBody').replaceChildren(node('p', message)); $('diffEvidence').replaceChildren();
    $('diffTargetTitle').textContent = '历史备份与源码快照';
    $('diffAddCount').textContent = '+0 行'; $('diffDelCount').textContent = '-0 行';
    $('diffArea').replaceChildren(new Option('全部范围', ''));
  }
  window.diffSync = function () {
    if (key() === targetKey) return;
    targetKey = key(); listSequence++; clear('工程或 PLC 已改变，请选择备份重新对比。');
    $('backupsList').replaceChildren(node('p', '请刷新当前工程的备份列表。'));
  };
  async function get(url, args) {
    if (!target().target_token) throw new Error('请等待工程与 PLC 识别完成。');
    const response = await fetch(url + '?' + new URLSearchParams(Object.assign(target(), args)), { cache: 'no-store' });
    const data = await response.json();
    if (!response.ok || data.error) throw new Error(data.error || '读取失败：' + response.status);
    return data;
  }
  window.loadBackups = async function () {
    window.diffSync(); if (typeof window.reviewRefresh === 'function') window.reviewRefresh(); const ticket = ++listSequence, scope = key();
    clear('请选择备份，与已读取的源码快照对比。');
    $('backupsList').replaceChildren(node('p', '正在加载备份…'));
    try {
      const list = await get('/api/backups');
      if (ticket !== listSequence || scope !== key()) return;
      if (!Array.isArray(list)) throw new Error('备份列表格式无效。');
      $('backupsList').replaceChildren();
      if (!list.length) $('backupsList').append(node('p', '当前工程与 PLC 暂无可用备份。'));
      list.forEach(item => {
        const button = node('button', '', 'backup-item'); button.type = 'button'; button.style.cssText = 'width:100%;text-align:left;color:inherit;background:transparent;';
        button.append(node('div', item.name || item.xml_file, 'backup-item-title'), node('div', (item.kind || '') + ' · ' + (item.group || '根目录'), 'backup-item-sub'), node('div', item.created_at || '', 'backup-item-sub'));
        button.addEventListener('click', () => window.viewDiff(item.xml_file, item.name, button));
        $('backupsList').append(button);
      });
    } catch (e) { if (ticket === listSequence && scope === key()) $('backupsList').replaceChildren(node('p', '加载失败：' + e.message)); }
  };
  window.viewDiff = async function (backup, name, button) {
    clear('正在计算差异…'); const ticket = sequence, scope = key();
    document.querySelectorAll('.backup-item').forEach(el => el.classList.toggle('active', el === button));
    $('diffTargetTitle').textContent = name || backup;
    try {
      const data = await get('/api/diff', { backup });
      if (ticket !== sequence || scope !== key()) return;
      if (!Array.isArray(data.lines) || !data.semantic) throw new Error('对比结果不完整。');
      result = data; mode = 'semantic';
      $('diffAddCount').textContent = '+' + data.additions + ' 行'; $('diffDelCount').textContent = '-' + data.deletions + ' 行';
      const e = data.evidence || {};
      $('diffEvidence').append(node('p', e.note || '只读源码快照对比。'), node('p', '备份时间：' + (e.backup_created_utc || '未知') + '；快照时间：' + (e.snapshot_indexed_utc || '未知')));
      const evidence = node('details', ''); evidence.append(node('summary', '工程范围与文件指纹'), node('pre', JSON.stringify(e, null, 2))); $('diffEvidence').append(evidence);
      const areas = [...new Set(data.semantic.changes.map(c => c.area))];
      areas.forEach(area => $('diffArea').append(new Option(area, area))); render();
    } catch (e) { if (ticket === sequence && scope === key()) $('diffBody').replaceChildren(node('p', '差异计算失败：' + e.message)); }
  };
  function render() {
    if (!result) return;
    const body = $('diffBody'); body.replaceChildren(); body.classList.toggle('semantic', mode === 'semantic');
    $('diffSemantic').classList.toggle('btn-primary', mode === 'semantic'); $('diffRaw').classList.toggle('btn-primary', mode === 'raw');
    $('diffArea').disabled = mode === 'raw';
    if (mode === 'semantic') {
      const semantic = result.semantic;
      semantic.warnings.forEach(w => body.append(node('p', w)));
      if (semantic.status === 'unsupported') { body.append(node('p', '此格式无法结构化对比，请切换到原始文本。')); return; }
      const changes = semantic.changes.filter(c => !$('diffArea').value || c.area === $('diffArea').value);
      body.append(node('p', '结构化变化 ' + semantic.changes.length + ' 项，当前范围 ' + changes.length + ' 项。'));
      if (!semantic.changes.length) body.append(node('p', '支持范围内未发现结构化变化；这不代表逻辑安全或完全等价，可查看原始文本确认。'));
      appendPaged(changes, c => {
        const detail = node('details', '');
        detail.append(node('summary', ({ add: '新增', delete: '删除', change: '修改' }[c.type] || c.type) + ' · ' + c.path));
        detail.addEventListener('toggle', () => {
          if (!detail.open || detail.dataset.loaded) return;
          detail.dataset.loaded = '1';
          detail.append(node('strong', '之前'), node('pre', c.before == null ? '（不存在）' : c.before), node('strong', '之后'), node('pre', c.after == null ? '（不存在）' : c.after));
        }); return detail;
      }, 100);
    } else {
      body.append(node('p', '原始文本：编号、空白和导出时间变化均保留。'));
      appendPaged(result.lines, line => {
        const row = node('div', '', 'diff-line ' + (['add', 'delete', 'same'].includes(line.type) ? line.type : 'same'));
        row.append(node('div', line.oldLine || '', 'diff-num'), node('div', line.newLine || '', 'diff-num'), node('div', (line.type === 'add' ? '+ ' : line.type === 'delete' ? '- ' : '  ') + line.text, 'diff-content')); return row;
      }, 500);
    }
    function appendPaged(items, make, size) {
      let offset = 0; const more = node('button', '加载更多', 'btn btn-sm');
      function add() {
        more.remove(); const end = Math.min(offset + size, items.length);
        while (offset < end) body.append(make(items[offset++]));
        if (offset < items.length) { more.textContent = '加载更多（已显示 ' + offset + '/' + items.length + '）'; body.append(more); }
      }
      more.addEventListener('click', add); add();
    }
  }
  $('diffSemantic').addEventListener('click', () => { mode = 'semantic'; render(); });
  $('diffRaw').addEventListener('click', () => { mode = 'raw'; render(); });
  $('diffArea').addEventListener('change', render);
})();
