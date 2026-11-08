const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){
  for(const location of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){
    try{return require(location);}catch(error){if(error.code!=='MODULE_NOT_FOUND')throw error;}
  }
  throw new Error('Install Playwright or set PLAYWRIGHT_MODULE.');
}
const {chromium}=playwright();
const index=fs.readFileSync(path.join(__dirname,'../web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const script=fs.readFileSync(path.join(__dirname,'../web/execution.js'),'utf8');
const applyScript=fs.readFileSync(path.join(__dirname,'../web/cylinder-apply.js'),'utf8');
const ids={source:'11111111111141118111111111111111',check:'22222222222242228222222222222222',recovery:'33333333333343338333333333333333',saved:'44444444444444448444444444444444'};
const at='2026-11-08T02:00:00.000Z';
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});let checks=0;
 async function fixture(mode='normal'){
  const context=await browser.newContext(),page=await context.newPage(),calls=[],errors=[];
  const source={id:ids.source,project:'E:\\工程 A\\Line.ap17',plc:'Station/PLC',state:'needs_review',stage:'finished',started:at,updated:at,cylinder_name:'夹紧气缸 <img src=x onerror=alert(1)>',instance_name:'Z04',can_check:true,message:'两个映射块已写入，气缸块未完成',events:[{stage:'backup',at,message:'备份已完成'},{stage:'write',at,message:'气缸块导入失败'}],result:{saved:false,instance_db_backups:[{block_name:'InstanceDB',path:'E:\\backup\\instance.xml'}],import_result:{error_count:0,warning_count:2,compile_report:'2 条工程原有警告',items:[{block_name:'Mapping',status:'imported',verification:'matched',backup:'E:\\backup\\mapping.xml'},{block_name:'Cylinder',status:'outcome_unknown',verification:'different',backup:'E:\\backup\\cylinder.xml'}]}}};
  const saved={...source,id:ids.saved,state:'completed',can_check:false,cylinder_name:'成功气缸',instance_name:'Z05',result:{saved:true,import_result:{ok:true,error_count:0,warning_count:6,compile_report:'6 条警告',items:[]}}};
  const check={...source,id:ids.check,parent_id:ids.source,recovery_action:'check',state:'completed',can_check:false,message:'核对完成',result:{saved:false,recovery_check:{source_id:ids.source,check_id:ids.check,can_continue:mode!=='conflict',can_restore:mode!=='conflict',reason:mode==='conflict'?'发现人工后续修改，不能覆盖':'核对通过',items:[{block_name:'Mapping',status:mode==='conflict'?'conflict':'planned',message:mode==='conflict'?'发现新接线':'与计划一致'}],continue_steps:['只补入未完成的气缸块','编译、回读并保存工程'],restore_steps:['恢复本次映射变更及关联实例 DB','编译、回读并保存工程']}}};
  const recovered={...source,id:ids.recovery,parent_id:ids.source,recovery_action:'restore',state:'completed',can_check:false,message:'恢复并保存完成',result:{saved:true,import_result:{ok:true,error_count:0,warning_count:0,items:[]}}};
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',async route=>{
    const url=new URL(route.request().url()),method=route.request().method();
    if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:index});
    if(!url.pathname.startsWith('/api/'))return route.fulfill({status:404,body:''});
    const body=method==='POST'?route.request().postDataJSON():null;
    calls.push({url:url.pathname,query:Object.fromEntries(url.searchParams),method,body});
    if(url.pathname==='/api/execution-log')return route.fulfill({json:{tasks:[{id:'log',operation_id:ids.source,source:'看板',action:'生成气缸',target:'工程 A',status:'failed',started:at,updated:at,message:'需要核对'}]}});
    if(url.pathname==='/api/io/cylinder-history')return route.fulfill({json:{jobs:Number(url.searchParams.get('offset'))?[saved]:[source,saved],total:31,offset:Number(url.searchParams.get('offset'))||0,limit:30}});
    if(url.pathname==='/api/io/cylinder-recovery-check'){
      if(mode==='rejected')return route.fulfill({status:409,json:{error:'后台仍在执行，请等待最终结果'}});
      if(mode==='lost')return route.abort();
      return route.fulfill({status:202,json:{id:ids.check}});
    }
    if(url.pathname==='/api/io/cylinder-recover'){
      if(mode==='recover-rejected')return route.fulfill({status:409,json:{error:'工程内容已变化，请重新核对'}});
      recovered.recovery_action=body.action;
      return route.fulfill({status:202,json:{id:ids.recovery}});
    }
    if(url.pathname==='/api/io/cylinder-apply-status'){
      const id=url.searchParams.get('id');
      if(id===ids.check || (!id && url.searchParams.get('request_id')))return route.fulfill({json:check});
      if(id===ids.recovery)return route.fulfill({json:recovered});
      if(id===ids.saved)return route.fulfill({json:saved});
      return route.fulfill({json:source});
    }
    return route.fulfill({status:404,json:{error:'unexpected route'}});
  });
  async function load(){
    await page.goto('http://localhost:18182/');
    await page.evaluate(()=>{window.cylinderApplying=false;window.invalidated=0;window.cylinderIoInvalidate=()=>window.invalidated++;window.cylinderSync=()=>{};});
    await page.addScriptTag({content:script});
    await page.addScriptTag({content:applyScript});
    await page.waitForFunction(()=>document.getElementById('engineeringHistorySummary').textContent.includes('共 31 条'));
  }
  await load();return{context,page,calls,errors,load};
 }
 try{
  let f=await fixture();
  await f.page.locator('#executionRows').getByRole('button',{name:'查看操作详情'}).click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('两个映射块已写入'));
  assert.match(await f.page.locator('#engineeringTaskBody').innerText(),/工程 A/);
  assert.equal(await f.page.locator('#engineeringTaskBody img').count(),0);checks++;
  assert.equal(await f.page.locator('#engineeringTaskBody').getByText('执行过程',{exact:true}).count(),1);
  await f.page.locator('#engineeringTaskBody summary').filter({hasText:'编译结果'}).click();
  assert.match(await f.page.locator('#engineeringTaskBody').innerText(),/2 条工程原有警告/);checks++;
  if(process.env.TIA_UI_SCREENSHOT)await f.page.locator('#engineeringHistory').screenshot({path:process.env.TIA_UI_SCREENSHOT});
  await f.load();await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('两个映射块已写入'));
  assert.equal(f.calls.filter(c=>c.method==='POST').length,0);checks++;
  await f.page.getByRole('button',{name:'核对当前工程',exact:true}).click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('核对通过'));
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-recovery-check')).length,1);
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-recover')).length,0);checks++;
  await f.page.getByRole('button',{name:'查看恢复方案',exact:true}).click();
  assert.match(await f.page.locator('.engineering-plan').innerText(),/其他未保存的修改/);
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-recover')).length,0);
  await f.page.getByRole('button',{name:'取消',exact:true}).click();
  assert.equal(await f.page.locator('.engineering-plan').count(),0);checks++;
  await f.page.getByRole('button',{name:'查看继续方案',exact:true}).click();
  assert.match(await f.page.locator('.engineering-plan').innerText(),/只补入未完成/);
  await f.page.getByRole('button',{name:'确认继续并保存工程',exact:true}).click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('恢复并保存完成'));
  const write=f.calls.find(c=>c.url.endsWith('cylinder-recover'));
  assert.equal(write.body.id,ids.source);assert.equal(write.body.check_id,ids.check);assert.equal(write.body.action,'continue');assert.equal(write.body.confirm_save,true);assert.equal(write.body.confirm,true);
  assert.equal(await f.page.evaluate(()=>window.invalidated),1);checks++;
  assert.equal(await f.page.locator('#cyApply').isDisabled(),false);assert.equal(await f.page.evaluate(()=>window.cylinderApplying),false);checks++;
  await f.page.locator('#engineeringHistoryNext').click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringHistoryPage').textContent==='31–31 / 31');
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-history')).at(-1).query.offset,'30');checks++;
  assert.deepEqual(f.errors,[]);await f.context.close();
  f=await fixture('conflict');await f.page.locator('#engineeringHistoryRows').getByRole('button',{name:'查看',exact:true}).first().click();
  await f.page.getByRole('button',{name:'核对当前工程',exact:true}).click();await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('发现人工后续修改'));
  assert.equal(await f.page.getByRole('button',{name:'查看继续方案',exact:true}).count(),0);assert.equal(await f.page.getByRole('button',{name:'查看恢复方案',exact:true}).count(),0);
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-recover')).length,0);checks++;await f.context.close();
  f=await fixture('rejected');await f.page.locator('#engineeringHistoryRows').getByRole('button',{name:'查看',exact:true}).first().click();await f.page.getByRole('button',{name:'核对当前工程',exact:true}).click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringTaskNotice').textContent.includes('后台仍在执行'));
  assert.match(await f.page.locator('#engineeringTaskBody').innerText(),/两个映射块已写入/);assert.equal(await f.page.evaluate(()=>window.cylinderRecoveryPending),false);checks++;await f.context.close();
  f=await fixture('lost');await f.page.locator('#engineeringHistoryRows').getByRole('button',{name:'查看',exact:true}).first().click();await f.page.getByRole('button',{name:'核对当前工程',exact:true}).click();
  await f.page.waitForFunction(()=>document.getElementById('engineeringTaskBody').textContent.includes('核对通过'));
  assert.equal(f.calls.filter(c=>c.url.endsWith('cylinder-recovery-check')).length,1);assert.ok(f.calls.some(c=>c.query.request_id));checks++;await f.context.close();
  console.log('PASS: '+checks+' execution history/recovery UI checks (durable reopen, scoped details, escaped text, warnings, linked tasks, explicit write confirmation, conflict, rejection, pagination and lost-response lookup). No TIA connection or writes.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
