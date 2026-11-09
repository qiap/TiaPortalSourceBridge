const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
const root=path.join(__dirname,'..'),source=fs.readFileSync(path.join(root,'web/index.html'),'utf8'),html=source.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,''),script=fs.readFileSync(path.join(root,'web/semantic-diff.js'),'utf8');
assert.match(source,/<script src="\/semantic-diff.js"><\/script>/);
const report=()=>({fileName:'Host',additions:1,deletions:1,lines:[{type:'delete',oldLine:1,text:'<img onerror=alert(1)>'},{type:'add',newLine:1,text:'new source'}],semantic:{status:'structural',warnings:['不判断逻辑等价'],changes:[{area:'接口',path:'接口/Static/Z01',type:'change',before:'Int',after:'DInt'},{area:'网络',path:'网络/1',type:'change',before:'<img src=x onerror=alert(1)>',after:'New'}]},evidence:{backup_created_utc:'2026-11-01',snapshot_indexed_utc:'2026-11-08',snapshot_sha256:'abc',note:'比较源码快照，不代表博途实时内容。'}});
(async()=>{
 const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=1;
 async function fixture(mode='normal'){
  const context=await browser.newContext(),page=await context.newPage(),calls=[],errors=[];let release,pending;
  await page.route('**/*',async route=>{
   const url=new URL(route.request().url()); if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   calls.push(Object.fromEntries(url.searchParams));
   if(url.pathname==='/api/backups'){
    if(mode==='list-error')return route.fulfill({status:409,json:{error:'工程无效 <img>'}});
    if(mode==='list-delay')await new Promise(r=>{release=r;pending=true;});
    return route.fulfill({json:[{xml_file:"batch/a'file.xml",name:"Host'<img>",kind:'Block',group:'Group',created_at:'2026-11-01'}]});
   }
   if(mode==='error')return route.fulfill({status:409,json:{error:'源码缺失，不表示已删除 <img>'}});
   if(mode==='delay'&&url.searchParams.get('backup')!=='second')await new Promise(r=>{release=r;pending=true;});
   const data=report();
   if(mode==='unsupported'){data.semantic.status='unsupported';data.semantic.changes=[];}
   if(mode==='empty')data.semantic.changes=[];
   if(mode==='many')data.semantic.changes=Array.from({length:205},(_,i)=>({...data.semantic.changes[0],path:'接口/'+i}));
   if(url.searchParams.get('backup')==='second')data.semantic.changes=[{area:'接口',path:'Second',type:'add',after:'second'}];
   return route.fulfill({json:data});
  });
  page.on('pageerror',e=>errors.push(e.message));await page.goto('http://localhost/');
  await page.evaluate(()=>{window.currentStatus={active_project:'E:/Test.ap17',target_plc:'PLC',target_token:'signed'};document.getElementById('tab-diff').classList.add('active');});
  await page.addScriptTag({content:script});
  return {page,context,calls,errors,release:()=>release(),waiting:()=>pending};
 }
 async function load(f){await f.page.evaluate(()=>loadBackups());}
 async function select(f){await f.page.locator('.backup-item').click();await f.page.waitForFunction(()=>!document.getElementById('diffBody').textContent.includes('正在计算'));}
 async function waiting(f){for(let i=0;i<200&&!f.waiting();i++)await new Promise(r=>setTimeout(r,5));assert.ok(f.waiting());}
 try{
  let f=await fixture();await load(f);assert.equal(await f.page.locator('.backup-item').count(),1);assert.equal(await f.page.locator('#backupsList img').count(),0);checks++;
  await select(f);assert.equal(f.calls[1].backup,"batch/a'file.xml");assert.equal(f.calls[1].target_token,'signed');assert.equal(f.calls[1].plc_name,'PLC');checks++;
  assert.match(await f.page.locator('#diffEvidence').textContent(),/2026-11-08/);assert.match(await f.page.locator('#diffEvidence').textContent(),/abc/);assert.equal(await f.page.locator('#diffBody details').count(),2);checks++;
  await f.page.locator('#diffArea').selectOption('网络');assert.equal(await f.page.locator('#diffBody details').count(),1);await f.page.locator('#diffBody summary').click();await f.page.locator('#diffBody details pre').first().waitFor({state:'visible'});assert.match(await f.page.locator('#diffBody').textContent(),/<img/);assert.equal(await f.page.locator('#diffBody img').count(),0);checks++;
  await f.page.locator('#diffRaw').click();assert.equal(await f.page.locator('.diff-line').count(),2);assert.equal(await f.page.locator('#diffArea').isDisabled(),true);assert.equal(await f.page.locator('#diffBody img').count(),0);checks++;
  await f.page.locator('#diffSemantic').click();assert.equal(await f.page.locator('#diffArea').isEnabled(),true);assert.deepEqual(f.errors,[]);checks++;await f.context.close();
  f=await fixture('error');await load(f);await select(f);assert.match(await f.page.locator('#diffBody').textContent(),/差异计算失败.*源码缺失/);assert.doesNotMatch(await f.page.locator('#diffBody').textContent(),/完全一致/);assert.equal(await f.page.locator('#diffBody img').count(),0);checks++;await f.context.close();
  f=await fixture('unsupported');await load(f);await select(f);assert.match(await f.page.locator('#diffBody').textContent(),/无法结构化/);assert.doesNotMatch(await f.page.locator('#diffBody').textContent(),/未发现结构化变化/);await f.page.locator('#diffRaw').click();assert.equal(await f.page.locator('.diff-line').count(),2);checks++;await f.context.close();
  f=await fixture('empty');await load(f);await select(f);assert.match(await f.page.locator('#diffBody').textContent(),/不代表逻辑安全/);checks++;await f.context.close();
  f=await fixture('list-error');await load(f);assert.match(await f.page.locator('#backupsList').textContent(),/加载失败/);assert.equal(await f.page.locator('#backupsList img').count(),0);checks++;await f.context.close();
  f=await fixture('many');await load(f);await select(f);assert.equal(await f.page.locator('#diffBody details').count(),100);await f.page.locator('#diffBody button').click();assert.equal(await f.page.locator('#diffBody details').count(),200);checks++;await f.context.close();
  f=await fixture('delay');await load(f);await f.page.locator('.backup-item').click();await waiting(f);await f.page.evaluate(()=>{currentStatus.target_plc='Other';diffSync();});f.release();await f.page.waitForTimeout(60);assert.match(await f.page.locator('#diffBody').textContent(),/已改变/);assert.equal(await f.page.locator('#diffBody details').count(),0);checks++;await f.context.close();
  f=await fixture('delay');await load(f);await f.page.locator('.backup-item').click();await waiting(f);await f.page.evaluate(()=>viewDiff('second','Second'));f.release();await f.page.waitForTimeout(60);assert.match(await f.page.locator('#diffBody').textContent(),/Second/);assert.doesNotMatch(await f.page.locator('#diffBody').textContent(),/Z01/);checks++;await f.context.close();
  f=await fixture('list-delay');await f.page.evaluate(()=>{loadBackups();});await waiting(f);await f.page.evaluate(()=>{currentStatus.active_project='E:/Other.ap17';diffSync();});f.release();await f.page.waitForTimeout(60);assert.equal(await f.page.locator('.backup-item').count(),0);checks++;await f.context.close();
  console.log('PASS: '+checks+' semantic diff UI checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
