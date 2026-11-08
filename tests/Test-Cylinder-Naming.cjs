const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const context={window:{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../web/cylinder-naming.js'),'utf8'),context);
const {plan}=context.window.CylinderNaming;
const p=(name,dir,leaf)=>({name,dir,binding:{value:'"DB".Input."'+leaf+'"',parts:[{name:'DB',indices:[]},{name:'Input',indices:[]},{name:leaf,indices:[]}]}});
const sample={pins:[p('i_Wpos','Input','旧气缸伸出位I52.6'),p('i_Hpos','Input','旧气缸缩回到位I52.7'),p('oOutput_W','Output','旧气缸伸出阀Q52.4'),p('oOutput_H','Output','旧气缸缩回阀Q52.5'),p('i_Interlock_Wpos','Input','站台急停I50.3')]};
const rules={i_Wpos:'input',i_Hpos:'input',oOutput_W:'output',oOutput_H:'output',i_Interlock_Wpos:'shared'};
const points=[{id:'i0',address:'%I53.0',area:'I',symbol:'"DB.IO".Input."I53.0"',availability:'unused',bank:'station'},
 {id:'i1',address:'%I53.1',area:'I',symbol:'"DB.IO".Input."I53.1"',availability:'unused'},
 {id:'q0',address:'%Q52.6',area:'Q',symbol:'"DB.IO".Output."Q52.6"',availability:'unused'},
 {id:'q1',address:'%Q52.7',area:'Q',symbol:'"SpareQ52.7"',availability:'unused'}];
const data={points};const bindings={i_Wpos:'i0',i_Hpos:'i1',oOutput_W:'q0',oOutput_H:'q1'};
const run=(name='侧面夹紧气缸',given=bindings)=>plan(data,sample,rules,given,name);
const result=run();assert.equal(result.renames.length,4);
assert.equal(result.points[0].symbol,'"DB.IO".Input."侧面夹紧气缸伸出位I53.0"');
assert.equal(result.points[1].symbol,'"DB.IO".Input."侧面夹紧气缸缩回到位I53.1"');
assert.equal(result.points[2].symbol,'"DB.IO".Output."侧面夹紧气缸伸出阀Q52.6"');
assert.equal(result.points[3].symbol,'"侧面夹紧气缸缩回阀Q52.7"');
assert.equal(result.points[0].bank,'station');assert.equal(data.points[0].symbol,'"DB.IO".Input."I53.0"');
assert.equal(result.renames[0].old_name,'I53.0');assert.equal(result.renames[0].kind,'mapped_member');assert.equal(result.renames[3].kind,'plc_tag');
assert.ok(result.warnings.some(x=>x.includes('重命名')));
assert.equal(run('测试',{}).renames.length,0);assert.equal(run('测试',{i_Wpos:'i0'}).renames.length,1);
assert.throws(()=>run('',{}),/气缸名称/);assert.throws(()=>run('bad\nname'),/单行/);assert.throws(()=>run('"bad'),/双引号/);
assert.throws(()=>run('a'.repeat(65)),/64/);
assert.throws(()=>run('测试',{i_Wpos:'missing'}),/失效/);
assert.throws(()=>run('测试',{i_Wpos:'q0'}),/方向/);
assert.throws(()=>run('测试',{i_Wpos:'i0',i_Hpos:'i0'}),/同一地址/);
data.points.push({...points[0],id:'alias',address:'E053.0'});assert.throws(()=>run('测试',{i_Wpos:'i0',i_Hpos:'alias'}),/同一地址/);data.points.pop();
data.points.push({id:'collision',address:'I54.0',symbol:'"db.io".Input."侧面夹紧气缸伸出位I53.0"',availability:'used'});
assert.throws(()=>run(),/已存在/);data.points.pop();
data.points[0].availability='used';assert.throws(()=>run(),/空闲/);data.points[0].availability='unused';
const unknown={pins:[p('Custom','Input','未知反馈')]};
assert.throws(()=>plan(data,unknown,{Custom:'input'},{Custom:'i0'},'测试'),/无法.*方向/);
const namedUnknown={pins:[p('Custom','Input','旧气缸缩回位I1.0')]};
assert.match(plan(data,namedUnknown,{Custom:'input'},{Custom:'i0'},'测试').points[0].symbol,/测试缩回位I53\.0/);
data.points[0].symbol='"DB.IO".Input."Array"[0].%X0';assert.throws(()=>run(),/切片/);
data.points[0].symbol='"DB.IO".Input."A""B.I53.0"';assert.equal(run().renames[0].old_name,'A"B.I53.0');
data.points[0].symbol='"DB.IO".Input.Spare';assert.equal(run().points[0].symbol,'"DB.IO".Input."侧面夹紧气缸伸出位I53.0"');
console.log('PASS: cylinder naming preserves donor suffixes, scoped symbols, physical addresses and metadata; rejects invalid names, duplicate points and symbol collisions.');
