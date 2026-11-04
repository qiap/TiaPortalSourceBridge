const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const context = {window:{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../web/cylinder-model.js'),'utf8'),context);
const {generate,roles}=context.window.CylinderModel;
const sample={id:'Z03',fb:'Cylinder.FB',pins:[]};
const model={instances:new Set(['z03']),declarations:{},occupied:new Set()};
const data={members:{Devices:{Z03:'"CylinderType"'},Alarms:{Timeout:'Array[1..32, 1..2] of Bool'}},
  points:[{id:'i',symbol:'"Feedback"',area:'I',address:'%I2.0',writes:[]},{id:'q',symbol:'"Valve"',area:'Q',address:'%Q2.0',writes:[]}],writes:{}};
function pin(name,dir,parts) {
  const components=parts.map(([name,indices=[]])=>({name,indices}));
  return {name,dir,type:'Bool',binding:{parts:components,value:parts.map(([name])=>name).join('.')}};
}
sample.pins=[pin('Sense','Input',[['OldInput']]),pin('Valve','Output',[['OldOutput']]),
  pin('Auto','Input',[['Devices'],['Z03'],['Auto']]),pin('Timeout','Output',[['Alarms'],['Timeout',['3','1']]])];
const rules={Sense:'input',Valve:'output',Auto:'device',Timeout:'index'};
const render=(name='Z04',index='4',points={Sense:'i',Valve:'q'})=>generate(data,model,sample,rules,name,points,index);
const draft=render();
assert.match(draft.code,/#Z04\(/);assert.match(draft.code,/"Devices"\.Z04\.Auto/);assert.match(draft.code,/"Alarms"\.Timeout\[4,1\]/);
assert.equal(draft.declarations.length,2);assert.equal(draft.diff.length,4);
assert.ok(!roles(sample.pins[2],sample).includes('shared'));
assert.throws(()=>render('z03'),/同名实例/);assert.throws(()=>render('x;END'),/气缸名/);
assert.throws(()=>render('Z04','99'),/越界/);assert.throws(()=>render('Z04','4',{Sense:'q',Valve:'i'}),/输入点/);
data.writes['Alarms.Timeout[4,1]']='OtherBlock';
assert.throws(()=>render(),/OtherBlock/);delete data.writes['Alarms.Timeout[4,1]'];
model.occupied.add('"valve"');assert.throws(()=>render(),/已接到/);
console.log('PASS: cylinder per-instance data, I/O directions, declarations, alarm bounds, existing writers and duplicate names.');
