// Run with node tests/Test-ProjectDropdown.cjs. No TIA connection required.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../web/index.html'), 'utf8');
const elements = Object.fromEntries(['runningProjectList', 'historyProjectList', 'runningCountBadge']
  .map(id => [id, { innerHTML: '' }]));
let selected;
const context = vm.createContext({
  document: { getElementById: id => elements[id] },
  selectProject: value => { selected = value; }
});
vm.runInContext(html.slice(html.indexOf('    function renderProjectDropdown('), html.indexOf('    function openProjectModal(')), context);
vm.runInContext(html.match(/    function escapeHtml\(str\) \{[\s\S]*?\n    \}/)[0], context);
const paths = [
  String.raw`E:\WORK\HD2303_OP280-OP3000PLC_V17_备份\HD2303_OP280-OP3000PLC_V17.ap17`,
  String.raw`C:\new\test\O'Brien & Sons\Line.ap21`,
  String.raw`\\server\工程 共享\Line.ap17`,
  String.raw`E:\quotes" &quot; <tag>\Line.ap17`
];
const decode = value => value.replace(/&(amp|quot|lt|gt);/g, (_, name) => ({ amp: '&', quot: '"', lt: '<', gt: '>' })[name]);
for (const isRunning of [true, false]) {
  for (const projectPath of paths) {
    context.renderProjectDropdown([{ path: projectPath, name: 'Test', is_running: isRunning }], '');
    const markup = elements[isRunning ? 'runningProjectList' : 'historyProjectList'].innerHTML;
    const tag = markup.match(/<div class="project-item [^>]+>/)[0];
    const attrs = Object.fromEntries([...tag.matchAll(/([\w-]+)="([^"]*)"/g)].map(m => [m[1], decode(m[2])]));
    const item = { dataset: { projectPath: attrs['data-project-path'] } };
    context.item = item;
    vm.runInContext(`(function () { ${attrs.onclick} }).call(item)`, context);
    assert.equal(selected, projectPath, 'Click must preserve the exact path');
  }
}
console.log('PASS: running and history project clicks preserve all 8 path cases.');
