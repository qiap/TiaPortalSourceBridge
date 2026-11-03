// No TIA connection: exercise refresh, stale responses, filters and target changes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
class Element {
  constructor() { this.children = []; this.style = {}; this.value = ''; this.textContent = ''; this.disabled = false; }
  appendChild(child) { this.children.push(child); }
  replaceChildren(...children) { this.children = children; }
  add(option) { this.children.push(option); }
  addEventListener(event, handler) { this[event] = handler; }
  scrollIntoView() {}
  click() { this.clicked = true; }
}
const elements = new Map();
const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
get('ioArea').value = 'IO';
const requests = [];
let revision = 0, dataDelay, exportCount = 0;
const fixture = () => ({ page: 1, page_size: 100, total: 120, total_all: 130, tables: ['//IO'], tables_read: 1, tables_total: 1,
  read_completed_utc: '2026-11-03T10:00:00Z', reference_source: 'test', reference_coverage: 'partial', warnings: [],
  rows: [{ id: 'motor', name: 'Motor ' + revision, address: '%I0.0', data_type: 'Bool', comment: '<script>text</script>', table: 'IO', reference_count: revision }] });
const context = vm.createContext({
  window: {}, currentStatus: { active_project: 'E:/A.ap17', target_plc: 'PLC1', target_token: 'signed' },
  document: { getElementById: get, createElement: () => new Element(), createDocumentFragment: () => new Element() },
  Option: function(text, value) { this.textContent = text; this.value = value; },
  URLSearchParams, URL: { createObjectURL: () => { exportCount++; return 'blob:test'; }, revokeObjectURL() {} },
  setTimeout: (fn, ms) => ms >= 10000 ? 0 : setTimeout(fn, 0), clearTimeout,
  fetch: async (url, options = {}) => {
    const u = new URL(url, 'http://localhost'); requests.push({ path: u.pathname, query: u.searchParams, options });
    let data;
    if (u.pathname.endsWith('/read')) { revision++; data = { id: 'job' + revision }; }
    else if (u.pathname.endsWith('/status')) data = { state: 'completed', message: 'done' };
    else if (u.pathname.endsWith('/data')) { data = fixture(); if (dataDelay) await dataDelay; }
    else if (u.pathname.endsWith('/references')) data = { name: 'Motor', total: 0, references: [], coverage: 'partial' };
    else if (u.pathname.endsWith('/export')) data = {};
    else throw new Error(url);
    return { ok: true, json: async () => data, blob: async () => ({}) };
  }
});
// Browser globals declared through window are also visible as globals.
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, '../web/io.js'), 'utf8'), context);
const tick = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  await context.ioRead();
  assert.equal(revision, 1);
  assert.equal(get('ioExportButton').disabled, false);
  assert.equal(get('ioRows').children[0].children[0].children[0].textContent, 'Motor 1');
  assert.equal(get('ioRows').children[0].children[0].children[3].textContent, '<script>text</script>');
  await context.ioRead();
  assert.equal(revision, 2, 'Refresh makes a new read request');
  assert.equal(get('ioRows').children[0].children[0].children[0].textContent, 'Motor 2');
  assert.equal(JSON.parse(requests.find(r => r.path.endsWith('/read')).options.body).plc_name, 'PLC1');
  get('ioSearch').value = 'pump'; get('ioArea').value = 'Q'; context.ioFilter(); await tick();
  assert.equal(requests.at(-1).query.get('q'), 'pump');
  assert.equal(requests.at(-1).query.get('area'), 'Q');
  await context.ioExport();
  assert.equal(exportCount, 1);
  assert.equal(requests.at(-1).query.get('page'), null, 'Export is not limited to visible page');
  let release; dataDelay = new Promise(resolve => { release = resolve; });
  context.ioFilter(); await tick();
  context.currentStatus.target_plc = 'PLC2'; context.ioTargetChanged();
  assert.equal(get('ioExportButton').disabled, true);
  release(); await tick();
  assert.match(get('ioRows').children[0].children[0].textContent, /重新读取/);
  await context.ioExport(); assert.equal(exportCount, 1, 'Old target cannot export');
  dataDelay = null; await context.ioRead();
  assert.equal(JSON.parse(requests.filter(r => r.path.endsWith('/read')).at(-1).options.body).plc_name, 'PLC2');
  assert.equal(get('ioExportButton').disabled, false);
  console.log('PASS: I/O panel refresh, filters, safe text, full export and stale target responses.');
})().catch(error => { console.error(error); process.exitCode = 1; });
