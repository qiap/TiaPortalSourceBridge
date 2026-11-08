const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const context = {window:{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../web/cylinder-model.js'),'utf8'),context);
const {generate,roles,availablePoints,pointAddress}=context.window.CylinderModel;
const sample={id:'Z03',fb:'Cylinder.FB',pins:[]};
const model={instances:new Set(['z03']),declarations:{},occupied:new Set()};
const data={members:{Devices:{Z03:'"CylinderType"'},Alarms:{Timeout:'Array[1..32, 1..2] of Bool'}},
  points:[{id:'i',symbol:'"Feedback"',area:'I',address:'%I2.0',availability:'unused',writes:[]},{id:'q',symbol:'"Valve"',area:'Q',address:'%Q2.0',availability:'unused',writes:[]}],writes:{}};
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
model.occupied.clear();
sample.pins.push({name:'UnusedFeedback',dir:'Input',type:'Bool',binding:{value:'未连接',parts:[],open:true}},
  {name:'UnusedValve',dir:'Output',type:'Bool',binding:{value:'未连接',parts:[],open:true}});
rules.UnusedFeedback=rules.UnusedValve='open';
for(const p of sample.pins.filter(p=>p.binding.open)) assert.deepEqual(Array.from(roles(p,sample)),['open']);
const openDraft=render();
assert.doesNotMatch(openDraft.code,/UnusedFeedback|UnusedValve/);
assert.equal(openDraft.diff.filter(p=>p.rule==='open').length,2);
assert.ok(openDraft.warnings.some(w=>w.includes('UnusedFeedback')));
assert.ok(openDraft.warnings.some(w=>w.includes('UnusedValve')));
assert.ok(!roles(sample.pins[0],sample).includes('open'));
assert.throws(()=>generate(data,model,sample,{...rules,Sense:'open'},'Z04',{Sense:'i',Valve:'q'},'4'));
assert.throws(()=>generate(data,model,{...sample,supported:false,reason:'附加触点'},rules,'Z04',{Sense:'i',Valve:'q'},'4'));
data.points[0].availability='used';assert.throws(()=>render(),/已使用/);
data.points[0].availability='unknown';assert.throws(()=>render(),/尚未核实/);
delete data.points[0].availability;assert.throws(()=>render(),/尚未核实/);
data.points[0].availability='unused';
data.points.push({id:'alias',symbol:'"InputAlias"',area:'I',address:'E02.0',availability:'used',writes:[]});
assert.throws(()=>render(),/已使用/);data.points.pop();
sample.pins.push(pin('OtherSense','Input',[['OtherOldInput']]));rules.OtherSense='input';
assert.throws(()=>render('Z04','4',{Sense:'i',OtherSense:'i',Valve:'q'}),/同一地址/);
sample.pins.pop();delete rules.OtherSense;
assert.equal(pointAddress('%E052.7'),'I52.7');assert.equal(pointAddress('%a0053.0'),'Q53.0');assert.equal(pointAddress('I53.8'),'');
const pt=(id,address,availability='unused',bank='OP275',extra={})=>({id,address,availability,bank,symbol:'"'+id+'"',area:'I',writes:[],...extra});
const pointData={points:[
  pt('old','%I52.7','used'),pt('earlier','I52.1'),pt('nextByte','I53.0'),pt('farther','I54.0'),
  pt('foreign','I52.6','unused','OP280'),pt('unknown','I53.1','unknown'),pt('used','I53.2','used'),
  pt('aliasRaw','E053.3'),pt('aliasUsed','I53.3','used'),
  pt('duplicateRaw','I53.4'),pt('duplicateMapped','%I53.4','unused','OP275',{mapping_fc:'Map'}),
  pt('oldShared','I90.0','used'),pt('output','Q55.0','unused','OP275',{area:'Q'})
]};
const pointModel={occupied:new Set(),endpointSymbols:[{fb:sample.fb,pin:'i_Wpos',symbol:'"old"'}],hostSymbols:new Set(['old','oldshared'])};
const available=availablePoints(pointData,pointModel,sample,'I');
assert.deepEqual(Array.from(available,p=>p.id),['nextByte','duplicateMapped','farther','earlier','foreign']);
assert.equal(available[0].after_address,'I52.7');assert.equal(available[0].recommended,true);
assert.equal(available.find(p=>p.id==='earlier').recommended,false);
assert.equal(available.find(p=>p.id==='foreign').recommended,false);
assert.equal(available.filter(p=>p.address_key==='I53.4').length,1);
pointModel.hostSymbols.add('nextbyte');assert.ok(!availablePoints(pointData,pointModel,sample,'I').some(p=>p.id==='nextByte'));
console.log('PASS: cylinder drafts, open pins, unused physical aliases, duplicate inputs, address ordering and station priority.');
