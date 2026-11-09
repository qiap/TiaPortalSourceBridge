const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,''),script=fs.readFileSync(path.join(root,'web/source-review.js'),'utf8');
(async()=>{
 const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=0;
 async function fixture(mode='normal'){
  const context=await browser.newContext(),page=await context.newPage(),calls=[],errors=[];let release;
  await page.route('**/*',async route=>{
   const url=new URL(route.request().url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   const action=url.pathname.split('/').pop(),data=route.request().method()==='POST'?route.request().postDataJSON():Object.fromEntries(url.searchParams);calls.push({action,data});
   if(mode==='error')return route.fulfill({status:409,json:{error:'快照损坏 <img src=x>'}});
   if(action==='list')return route.fulfill({json:[{id:'a',label:'Before <img>',created_utc:'2026',count:3,warnings:0},{id:'b',label:'After',created_utc:'2026',count:3,warnings:1}]});
   if(action==='compare'){
    if(mode==='delay')await new Promise(r=>release=r);
    return route.fulfill({json:{before_id:'a',after_id:'b',export_noise_only:1,warnings:['没有覆盖 HMI <img>'],objects:Array.from({length:mode==='many'?201:1},(_,i)=>({id:'x'+i,name:'Leaf'+i,group:'Group',unit:'',type:'modified',changes:1})),impact:[{changed:'x0',caller_name:'Main',side:'before',path:['x0','main']}]}});
   }
   if(action==='object')return route.fulfill({json:{semantic:{status:'structural',warnings:[],changes:[{type:'change',path:'Name',before:'<img src=x>',after:'new'}]},lines:[{type:'delete',text:'<img src=x>'}]}});
   if(action==='capture')return route.fulfill({json:{id:'b',count:3,warnings:[]}});
   return route.fulfill({status:404,json:{error:'unexpected route'}});
  });
  page.on('pageerror',e=>errors.push(e.message));await page.goto('http://localhost/');await page.evaluate(()=>{window.currentStatus={active_project:'E:/Fixture.ap17',target_plc:'PLC',target_token:'token'};document.getElementById('tab-diff').classList.add('active');});await page.addScriptTag({content:script});
  return{page,context,calls,errors,release:()=>release?.(),pending:()=>!!release};
 }
 async function select(f){await f.page.evaluate(()=>reviewRefresh());await f.page.locator('#reviewBefore').selectOption('a');await f.page.locator('#reviewAfter').selectOption('b');}
 try{
  let f=await fixture();await select(f);assert.equal(await f.page.locator('#reviewBefore option').count(),3);assert.equal(await f.page.locator('#reviewBefore img').count(),0);checks++;
  await f.page.locator('#reviewCompare').click();await f.page.waitForFunction(()=>document.getElementById('reviewStatus').textContent.includes('变化对象'));assert.equal(f.calls.at(-1).data.target_token,'token');assert.equal(f.calls.at(-1).data.before_id,'a');checks++;
  assert.match(await f.page.locator('#reviewResults').textContent(),/Main（之前，1 层）/);assert.equal(await f.page.locator('#reviewResults img').count(),0);checks++;
  await f.page.locator('#reviewResults > details').nth(1).locator('summary').first().click();await f.page.getByRole('button',{name:'查看对象变化',exact:true,includeHidden:true}).click();await f.page.waitForFunction(()=>document.querySelector('#reviewResults pre'));assert.match(await f.page.locator('#reviewResults').textContent(),/<img src=x>/);assert.equal(await f.page.locator('#reviewResults img').count(),0);checks++;
  await f.page.locator('#reviewSearch').fill('unmatched');assert.equal(await f.page.getByRole('button',{name:'查看对象变化',exact:true,includeHidden:true}).count(),0);checks++;
  await f.page.locator('#reviewBefore').selectOption('b');assert.equal(await f.page.locator('#reviewResults').textContent(),'');checks++;
  await f.page.locator('#reviewLabel').fill('Checkpoint');await f.page.locator('#reviewCapture').click();await f.page.waitForFunction(()=>document.getElementById('reviewStatus').textContent.includes('已保存'));assert.equal(f.calls.find(c=>c.action==='capture').data.label,'Checkpoint');assert.deepEqual(f.errors,[]);checks++;await f.context.close();
  f=await fixture('error');await f.page.evaluate(()=>reviewRefresh());assert.match(await f.page.locator('#reviewStatus').textContent(),/操作失败.*快照损坏/);assert.equal(await f.page.locator('#reviewStatus img').count(),0);checks++;await f.context.close();
  f=await fixture('many');await select(f);await f.page.locator('#reviewCompare').click();await f.page.waitForFunction(()=>document.getElementById('reviewStatus').textContent.includes('变化对象'));assert.equal(await f.page.getByRole('button',{name:'查看对象变化',exact:true,includeHidden:true}).count(),100);await f.page.getByRole('button',{name:/加载更多/}).click();assert.equal(await f.page.getByRole('button',{name:'查看对象变化',exact:true,includeHidden:true}).count(),200);checks++;await f.context.close();
  f=await fixture('delay');await select(f);await f.page.locator('#reviewCompare').click();while(!f.pending())await new Promise(r=>setTimeout(r,10));await f.page.evaluate(()=>{currentStatus.target_plc='Other';reviewSync();});f.release();await f.page.waitForFunction(()=>!document.getElementById('reviewRefresh').disabled);assert.equal(await f.page.locator('#reviewResults').textContent(),'');assert.equal(await f.page.locator('#reviewBefore option').count(),0);checks++;await f.context.close();
  console.log('PASS: '+checks+' source snapshot review UI checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
