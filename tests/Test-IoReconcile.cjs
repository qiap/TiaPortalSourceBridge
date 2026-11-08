const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,''),script=fs.readFileSync(path.join(root,'web/io-reconcile.js'),'utf8');
const target={project_path:'E:\\工程\\Fixture.ap17',plc_name:'CPU',target_token:'token'};
const preview={sheet:'点表',header_row:2,columns:[{index:0,letter:'A',title:'地址'},{index:1,letter:'B',title:'名称'},{index:2,letter:'C',title:'类型'},{index:3,letter:'D',title:'注释'}],suggested:{address:0,name:1,type:2,comment:3},samples:[{row:3,values:['%Q0.0','<img src=x onerror=alert(1)>','Bool','comment']}]};
const choices={scopes:[{scope:'native',units:[{name:'',count:2,tables:[{key:'IO\nTags',name:'IO / Tags',count:2}]},{name:'U2',count:1,tables:[{key:'IO\nOther',name:'IO / Other',count:1}]}]},{scope:'mapped',units:[{name:'',count:1,tables:[{key:'IO\nMapped',name:'IO / Mapped',count:1}]}]}]};
const report=()=>({id:'report',snapshot_id:'snapshot',...target,file_name:'input.xlsx',source_mode:'local_cache',snapshot_started:'2026-11-08T01:00:00Z',snapshot_completed:'2026-11-08T01:01:00Z',options:{sheet:'点表',header_row:2,scope:'native',unit:'',table:''},excel_rows:2,plc_rows:2,counts:{match:1,different:1},warnings:['只读；未修改工程'],rows:[
 {excel_row:3,status:'different',address:'Q0.0',name:'<img src=x onerror=alert(1)>',type:'Bool',comment:'new',issues:['名称不同'],plc:[{address:'%Q0.0',name:'OldValve',type:'Bool',comment:'old',unit:'',group:'IO',table:'Tags',mapping:'',references:2}]},
 {excel_row:4,status:'match',address:'I0.0',name:'Sensor',type:'Bool',comment:'',issues:[],plc:[{address:'I0.0',name:'Sensor',type:'Bool',comment:'',unit:'',group:'IO',table:'Tags',mapping:'',references:0}]}
]});
(async()=>{
 const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=0;
 async function fixture(mode='normal',snapshot='snapshot'){
  const context=await browser.newContext({acceptDownloads:true}),page=await context.newPage(),calls=[],errors=[];let release;
  await page.route('**/*',async route=>{
   const req=route.request(),url=new URL(req.url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   calls.push({path:url.pathname,query:Object.fromEntries(url.searchParams),body:req.method()==='POST'?req.postDataJSON():null});
   if(url.pathname.endsWith('reconcile-options'))return route.fulfill({json:{snapshot_id:url.searchParams.get('id'),...target,choices}});
   if(url.pathname.endsWith('reconcile-upload')){
    if(mode==='upload-delay')await new Promise(r=>release=r);
    if(mode==='upload-error')return route.fulfill({status:400,json:{error:'Excel 文件损坏'}});
    return route.fulfill({json:{upload_id:'upload',...target,file_name:'input.xlsx',sheets:[{name:'点表',rows:3},{name:'备用表',rows:2,hidden:true}],preview}});
   }
   if(url.pathname.endsWith('reconcile-preview'))return route.fulfill({json:{...preview,sheet:url.searchParams.get('sheet'),header_row:Number(url.searchParams.get('header_row'))||1}});
   if(url.pathname.endsWith('reconcile-run')){
    if(mode==='run-delay')await new Promise(r=>release=r);
    if(mode==='run-error')return route.fulfill({status:409,json:{error:'快照已失效'}});
    const r=report();if(mode==='wrong-report')r.plc_name='Wrong';if(mode==='pages'){r.rows=Array.from({length:53},(_,i)=>({...r.rows[0],excel_row:3+i}));r.counts={different:53};}
    return route.fulfill({json:r});
   }
   if(url.pathname.endsWith('reconcile-export')){if(mode==='export-delay')await new Promise(r=>release=r);return route.fulfill({contentType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',body:Buffer.from('PKfixture')});}
   throw Error('Unexpected endpoint '+url.pathname);
  });
  page.on('pageerror',e=>errors.push(e.message));await page.goto('http://localhost/');
  await page.evaluate(({target,snapshot})=>{window.ioFixture={target,id:snapshot,busy:false};window.cylinderIoState=()=>window.ioFixture;window.readModes=[];window.ioRead=async mode=>{window.readModes.push(mode);window.ioFixture.id='new-snapshot';};document.getElementById('tab-io').classList.add('active');},{target,snapshot});
  await page.addScriptTag({content:script});
  return {page,context,calls,errors,release:()=>release(),waiting:()=>!!release};
 }
 async function upload(f){await f.page.locator('#recFile').setInputFiles({name:'input.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:Buffer.from('PKtest')});await f.page.locator('#recUpload').click();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('Excel 已读取'));}
 async function run(f){await f.page.locator('#recRun').click();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('只读对账完成'));}
 async function waitPending(f){for(let i=0;i<200&&!f.waiting();i++)await new Promise(r=>setTimeout(r,5));assert.ok(f.waiting());}
 try{
  let f=await fixture();assert.equal(await f.page.locator('#recRun').isDisabled(),true);checks++;
  await upload(f);assert.equal(await f.page.locator('#recHeader').inputValue(),'2');assert.equal(await f.page.locator('#recAddress').inputValue(),'0');assert.equal(await f.page.locator('#recName').inputValue(),'1');assert.equal(await f.page.locator('#recRun').isEnabled(),true);checks++;
  assert.equal(await f.page.locator('#recSample img').count(),0);assert.match(await f.page.locator('#recSample').textContent(),/<img/);assert.equal(f.calls.find(c=>c.path.endsWith('upload')).body.content_base64,Buffer.from('PKtest').toString('base64'));checks++;
  await f.page.locator('#recUnit').selectOption('U2');assert.match(await f.page.locator('#recTable').textContent(),/Other/);await f.page.locator('#recUnit').selectOption('');checks++;
  await run(f);const request=f.calls.find(c=>c.path.endsWith('run')).body;assert.equal(request.id,'snapshot');assert.equal(request.upload_id,'upload');assert.deepEqual(request.options,{sheet:'点表',header_row:2,scope:'native',unit:'',table:'',address:0,name:1,type:2,comment:3});checks++;
  assert.equal(await f.page.locator('#recRows tr').count(),1);assert.match(await f.page.locator('#recRows').textContent(),/OldValve/);assert.equal(await f.page.locator('#recRows img').count(),0);assert.match(await f.page.locator('#recReportSource').textContent(),/本地缓存源码/);checks++;
  await f.page.locator('#recFilter').selectOption('all');assert.equal(await f.page.locator('#recRows tr').count(),2);await f.page.locator('#recSearch').fill('Sensor');assert.equal(await f.page.locator('#recRows tr').count(),1);checks++;
  const download=f.page.waitForEvent('download');await f.page.locator('#recExport').click();assert.match((await download).suggestedFilename(),/电气点表对账.*\.xlsx/);assert.deepEqual(f.calls.find(c=>c.path.endsWith('export')).query,{id:'report'});checks++;
  await f.page.locator('#recComment').selectOption('-1');assert.equal(await f.page.locator('#recReport').isHidden(),true);assert.equal(await f.page.locator('#recExport').isDisabled(),true);checks++;
  await run(f);await f.page.evaluate(()=>{window.ioFixture.id='new';window.ioReconcileSync();});assert.equal(await f.page.locator('#recReport').isHidden(),true);assert.equal(await f.page.locator('#recMapping').isVisible(),true);checks++;
  await f.page.evaluate(()=>{window.ioFixture.target={...window.ioFixture.target,plc_name:'Other'};window.ioReconcileSync();});assert.equal(await f.page.locator('#recMapping').isHidden(),true);assert.equal(await f.page.locator('#recRun').isDisabled(),true);assert.deepEqual(f.errors,[]);checks++;await f.context.close();
  f=await fixture();await upload(f);await f.page.locator('#recHeader').fill('4');await f.page.locator('#recRun').click();assert.match(await f.page.locator('#recStatus').textContent(),/更新表头/);assert.equal(f.calls.filter(c=>c.path.endsWith('run')).length,0);await f.page.locator('#recPreview').click();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('预览已更新'));assert.equal(await f.page.locator('#recHeader').inputValue(),'4');checks++;
  await f.page.locator('#recSheet').selectOption('备用表');await f.page.waitForFunction(()=>document.getElementById('recHeader').value==='1');assert.equal(f.calls.filter(c=>c.path.endsWith('preview')).at(-1).query.header_row,'0');checks++;await f.context.close();
  f=await fixture('run-delay');await upload(f);await f.page.locator('#recRun').click();await waitPending(f);assert.equal(await f.page.locator('#recRun').isDisabled(),true);await f.page.evaluate(()=>{window.ioFixture.id='different';window.ioReconcileSync();});f.release();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.includes('丢弃'));assert.equal(await f.page.locator('#recReport').isHidden(),true);checks++;await f.context.close();
  f=await fixture('upload-delay');await f.page.locator('#recFile').setInputFiles({name:'input.xlsx',buffer:Buffer.from('PKtest')});await f.page.locator('#recUpload').click();await waitPending(f);await f.page.evaluate(()=>{window.ioFixture.target={...window.ioFixture.target,plc_name:'Other'};window.ioReconcileSync();});f.release();await f.page.waitForTimeout(100);assert.equal(await f.page.locator('#recMapping').isHidden(),true);checks++;await f.context.close();
  for(const mode of ['run-error','wrong-report']){f=await fixture(mode);await upload(f);await f.page.locator('#recRun').click();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('对账失败'));assert.equal(await f.page.locator('#recReport').isHidden(),true);assert.equal(await f.page.locator('#recRun').isEnabled(),true);checks++;await f.context.close();}
  f=await fixture('export-delay');await upload(f);await run(f);let downloaded=false;f.page.on('download',()=>downloaded=true);await f.page.locator('#recExport').click();await waitPending(f);await f.page.evaluate(()=>{window.ioFixture.id='new';window.ioReconcileSync();});f.release();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('导出失败'));assert.equal(downloaded,false);checks++;await f.context.close();
  f=await fixture('pages');await upload(f);await run(f);assert.equal(await f.page.locator('#recRows tr').count(),50);await f.page.locator('#recNext').click();assert.equal(await f.page.locator('#recRows tr').count(),3);assert.match(await f.page.locator('#recPage').textContent(),/2 \/ 2/);checks++;await f.context.close();
  f=await fixture();await upload(f);await f.page.evaluate(()=>{window.cylinderApplying=true;window.ioReconcileSync();document.getElementById('recRun').onclick();});assert.equal(f.calls.filter(c=>c.path.endsWith('run')).length,0);checks++;await f.context.close();
  f=await fixture('normal','');await f.page.locator('#recCached').click();await f.page.waitForFunction(()=>document.getElementById('recStatus').textContent.startsWith('PLC 点表已就绪'));assert.deepEqual(await f.page.evaluate(()=>window.readModes),['source']);checks++;await f.context.close();
  console.log('PASS: '+checks+' read-only I/O reconciliation UI checks (mapping, preview, filters, export, XSS, paging, stale target/snapshot and errors).');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
