// Uses an isolated browser with mocked read-only services; never connects to TIA.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const location of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(location);}catch(error){if(error.code!=='MODULE_NOT_FOUND')throw error;}}throw Error('Install Playwright or set PLAYWRIGHT_MODULE.');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const script=fs.readFileSync(path.join(root,'web/audit.js'),'utf8');
const target={project_path:'E:\\工程 A\\Fixture.ap17',plc_name:'Station/CPU',target_token:'test-token'};
const makeReport=()=>({schema:1,id:'report',...target,snapshot_started:'2026-11-08T01:00:00Z',snapshot_completed:'2026-11-08T01:01:00Z',generated_at:'2026-11-08T01:02:00Z',coverage:{programs_total:2,programs_read:1,tag_tables_total:4,tag_tables_read:3,blocks_total:10,blocks_read:9,blocks_parsed:8,native_tags:100,addresses_checked:80,templates:1,template_calls_checked:3,supported_write_sites:7,unsupported_blocks:1,source_mode:'local_cache',source_note:'本地缓存原始读取范围：2026-11-06T01:00:00Z 至 2026-11-07T02:00:00Z',truncated:false,limitations:['SCL 控制流未覆盖']},warnings:['一个块读取失败'],findings:[
 {id:'a',rule:'address_overlap',severity:'warning',confidence:'confirmed',title:'声明地址重叠',subject:'MB10 / M10.3',explanation:'重叠可能是合法别名，请核对。',suggestion:'查看位范围和使用方式。',locations:[{block:'Tags',group:'Variables',unit:'',network:'',location:'row 1',symbol:'TestTag',address:'MB10',access:'declaration',evidence:'M10.0–M10.7'}]},
 {id:'b',rule:'multiple_writes',severity:'info',confidence:'review',title:'同一状态位的置位与复位',subject:'"DB".Flag',explanation:'可能为有意设计。',suggestion:'核对条件与执行顺序。',locations:[{block:'Sequence',group:'Station',unit:'',network:'2',location:'wire 1',symbol:'"DB".Flag',address:'',access:'SCoil',evidence:'native pin SCoil.operand'}]},
 {id:'c',rule:'template_pin',severity:'warning',confidence:'confirmed',title:'模板参数未连接 <img src=x onerror=alert(1)>',subject:'Z04.i_Wpos',explanation:'<script>alert(1)</script>',suggestion:'核对模板规则。',locations:[{block:'Cylinder',group:'Station',unit:'Unit A',network:'4',location:'call',symbol:'<svg onload=alert(1)>',address:'',access:'Input',evidence:'OpenCon'}]}
]});
(async()=>{
 const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=0;
 async function fixture(mode='normal',initial='snapshot'){
  const context=await browser.newContext({acceptDownloads:true}),page=await context.newPage(),calls=[],errors=[];let release=null,polls=0;
  const report=makeReport();if(mode==='zero')report.findings=[];
  if(mode==='live-report')report.coverage.source_mode='tia_readback';
  if(mode==='access')report.findings[0].locations=['declaration','writes','reads_writes','connection'].map(access=>({...report.findings[0].locations[0],access}));
  if(mode==='pages')report.findings=Array.from({length:53},(_,i)=>({...report.findings[0],id:String(i),subject:'Tag'+i}));
  await page.route('**/*',async route=>{
   const request=route.request(),url=new URL(request.url());
   if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   calls.push({path:url.pathname,method:request.method(),query:Object.fromEntries(url.searchParams),body:request.method()==='POST'?request.postDataJSON():null});
   if(url.pathname==='/api/io/audit-run'){
    if(mode==='rejected')return route.fulfill({status:409,json:{error:'源码快照已过期'}});
    return route.fulfill({status:202,json:{id:'job'}});
   }
   if(url.pathname==='/api/io/audit-status'){
    polls++;
    if(mode==='delayed')await new Promise(resolve=>{release=resolve;});
    if(mode==='running'&&polls===1)return route.fulfill({json:{id:'job',...target,state:'running',message:'正在扫描程序块'}});
    if(mode==='failed')return route.fulfill({json:{id:'job',...target,state:'failed',error:'读取源码失败'}});
    return route.fulfill({json:{id:'job',...target,state:'completed',result:mode==='wrong'?{...report,plc_name:'OtherPLC'}:report}});
   }
   if(url.pathname==='/api/io/audit-export'){
    if(mode==='export-delayed')await new Promise(resolve=>{release=resolve;});
    return route.fulfill({contentType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',body:'mock workbook'});
   }
   return route.fulfill({status:404,json:{error:'unexpected endpoint'}});
  });
  page.on('pageerror',error=>errors.push(error.message));await page.goto('http://audit.test/');
  await page.evaluate(({target,initial,mode})=>{
   window.auditFixture={id:initial,target,busy:false};window.cylinderIoState=()=>window.auditFixture;window.readModes=[];
   window.ioRead=async readMode=>{window.readModes.push(readMode);if(mode==='read-noop'){document.getElementById('ioMessage').textContent='读取没有启动';return;}if(mode==='read-throws')throw Error('读取异常：测试原因');window.auditFixture.id='';window.auditFixture.busy=true;window.auditSync?.();await Promise.resolve();window.auditFixture.busy=false;window.auditFixture.id=mode==='read-failed'?'':'snapshot-new';document.getElementById('ioMessage').textContent=mode==='read-failed'?'读取失败：测试原因':'读取完成';};
   document.querySelectorAll('.tab-content').forEach(e=>e.classList.remove('active'));document.getElementById('tab-audit').classList.add('active');
  },{target,initial,mode});
  await page.addScriptTag({content:script});
  return{page,context,calls,errors,release:()=>release?.(),hasRelease:()=>!!release};
 }
 async function complete(f,button='#auditRun'){await f.page.locator(button).click();await f.page.waitForFunction(()=>document.getElementById('auditStatus').textContent.startsWith('体检完成'));}
 try{
  let f=await fixture('normal','');
  assert.equal(await f.page.locator('#auditRun').isDisabled(),true);assert.equal(await f.page.locator('#auditExport').isDisabled(),true);assert.equal(f.calls.length,0);checks++;
  await complete(f,'#auditCached');assert.deepEqual(await f.page.evaluate(()=>window.readModes),['source']);assert.deepEqual(f.calls.find(c=>c.method==='POST').body,{id:'snapshot-new',...target});assert.equal(f.calls.filter(c=>c.method==='POST').length,1);checks++;
  assert.match(await f.page.locator('#auditSummary').textContent(),/共 3 项发现/);assert.match(await f.page.locator('#auditCoverage').textContent(),/已读取程序块：9/);assert.match(await f.page.locator('#auditLimitList').textContent(),/SCL 控制流未覆盖/);assert.match(await f.page.locator('#auditLimitList').textContent(),/读取失败/);checks++;
  assert.equal(await f.page.locator('#auditLimits').evaluate(e=>e.open),false);assert.match(await f.page.locator('#auditLimitsSummary').textContent(),/2 条说明/);assert.equal(await f.page.locator('#auditLimitList').isVisible(),false);assert.match(await f.page.locator('#auditCoverage').textContent(),/部分或全部行为未覆盖的块：1/);await f.page.locator('#auditLimitsSummary').click();assert.equal(await f.page.locator('#auditLimitList').isVisible(),true);await f.page.locator('#auditLimitsSummary').click();
  assert.match(await f.page.locator('#auditCoverage').textContent(),/PLC 程序总数：2.*已读取 PLC 程序：1.*变量表总数：4.*已读取变量表：3/);assert.match(await f.page.locator('#auditSource').textContent(),/本地缓存源码（本次未重新读取博途）/);assert.match(await f.page.locator('#auditSourceNote').textContent(),/2026-11-06T01:00:00Z 至 2026-11-07T02:00:00Z/);assert.match(await f.page.locator('#auditSnapshot').textContent(),/^本次读取\/整理时间：/);checks++;
  assert.equal(await f.page.locator('#auditReport img,#auditReport svg,#auditReport script').count(),0);assert.match(await f.page.locator('#auditFindings').textContent(),/<img src=x/);checks++;
  if(process.env.AUDIT_UI_SCREENSHOT)await f.page.screenshot({path:process.env.AUDIT_UI_SCREENSHOT,fullPage:true});
  await f.page.locator('#auditSeverity').selectOption('info');assert.equal(await f.page.locator('.audit-finding').count(),1);await f.page.locator('#auditSeverity').selectOption('all');await f.page.locator('#auditRule').selectOption('template_pin');assert.equal(await f.page.locator('.audit-finding').count(),1);checks++;
  await f.page.locator('#auditRule').selectOption('all');await f.page.locator('#auditSearch').fill('SCoil.operand');assert.equal(await f.page.locator('.audit-finding').count(),1);await f.page.locator('.audit-finding summary').click();assert.match(await f.page.locator('.audit-evidence:visible').textContent(),/网络 2/);checks++;
  const downloadPromise=f.page.waitForEvent('download');await f.page.locator('#auditExport').click();const download=await downloadPromise;assert.match(download.suggestedFilename(),/程序体检.*\.xlsx$/);assert.deepEqual(f.calls.find(c=>c.path.endsWith('audit-export')).query,{id:'job'});checks++;
  await f.page.evaluate(()=>{window.auditFixture={...window.auditFixture,id:'other-snapshot',target:{project_path:'E:\\Other.ap17',plc_name:'Other/CPU',target_token:'other'}};window.auditSync();});
  assert.equal(await f.page.locator('#auditStale').isVisible(),true);assert.equal(await f.page.locator('#auditExport').isDisabled(),true);assert.match(await f.page.locator('#auditReportTarget').textContent(),/工程 A/);checks++;
  assert.deepEqual(f.errors,[]);await f.context.close();
  f=await fixture('live-report');await complete(f,'#auditLive');assert.deepEqual(await f.page.evaluate(()=>window.readModes),['live']);assert.ok(f.calls.every(c=>/^\/api\/io\/audit-(run|status)$/.test(c.path)));assert.match(await f.page.locator('#auditSource').textContent(),/从博途读取的源码快照/);checks++;await f.context.close();
  f=await fixture('delayed');await f.page.locator('#auditRun').click();while(!f.hasRelease())await new Promise(r=>setTimeout(r,5));
  assert.equal(await f.page.locator('#auditRun').isDisabled(),true);await f.page.evaluate(()=>{window.auditFixture.id='changed';window.auditSync();});f.release();await f.page.waitForTimeout(80);
  assert.equal(await f.page.locator('#auditReport').isHidden(),true);assert.match(await f.page.locator('#auditStatus').textContent(),/停止接收旧体检结果/);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);checks++;await f.context.close();
  f=await fixture('read-failed','');await f.page.locator('#auditCached').click();await f.page.waitForFunction(()=>document.getElementById('auditStatus').textContent.startsWith('无法开始体检'));assert.equal(f.calls.length,0);assert.match(await f.page.locator('#auditStatus').textContent(),/测试原因/);checks++;await f.context.close();
  for(const mode of ['read-noop','read-throws','read-failed']){
   f=await fixture(mode,'old-snapshot');await f.page.locator('#auditCached').click();await f.page.waitForFunction(()=>document.getElementById('auditStatus').textContent.startsWith('无法开始体检'));assert.equal(f.calls.length,0);assert.equal(await f.page.locator('#auditReport').isHidden(),true);if(mode==='read-noop')assert.match(await f.page.locator('#auditStatus').textContent(),/没有获得新的源码快照/);else assert.match(await f.page.locator('#auditStatus').textContent(),/测试原因/);checks++;await f.context.close();
  }
  for(const mode of ['rejected','failed','wrong']){
   f=await fixture(mode);await f.page.locator('#auditRun').click();await f.page.waitForFunction(()=>document.getElementById('auditStatus').textContent.startsWith('体检未完成'));assert.equal(await f.page.locator('#auditReport').isHidden(),true);assert.equal(await f.page.locator('#auditRun').isDisabled(),false);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);checks++;await f.context.close();
  }
  f=await fixture('zero');await complete(f);assert.match(await f.page.locator('#auditFindings').textContent(),/不能据此认定程序安全/);checks++;await f.context.close();
  f=await fixture('access');await complete(f);await f.page.locator('.audit-finding summary').first().click();assert.deepEqual(await f.page.locator('.audit-finding').first().locator('td:nth-child(3)').allTextContents(),['变量声明','写入','读取与写入','参数连接']);checks++;await f.context.close();
  f=await fixture('running');await complete(f);assert.equal(f.calls.filter(c=>c.path.endsWith('audit-status')).length,2);assert.equal(f.calls.filter(c=>c.method==='POST').length,1);checks++;await f.context.close();
  f=await fixture('pages');await complete(f);assert.equal(await f.page.locator('.audit-finding').count(),50);await f.page.locator('#auditNext').click();assert.equal(await f.page.locator('.audit-finding').count(),3);assert.match(await f.page.locator('#auditPage').textContent(),/2 \/ 2/);checks++;await f.context.close();
  f=await fixture();await f.page.evaluate(()=>{window.cylinderApplying=true;window.auditSync();document.getElementById('auditRun').onclick();document.getElementById('auditLive').onclick();});assert.equal(f.calls.length,0);assert.equal(await f.page.locator('#auditRun').isDisabled(),true);checks++;await f.context.close();
  f=await fixture('export-delayed');await complete(f);let downloaded=false;f.page.on('download',()=>downloaded=true);await f.page.locator('#auditExport').click();while(!f.hasRelease())await new Promise(r=>setTimeout(r,5));await f.page.evaluate(()=>{window.auditFixture.id='new';window.auditSync();});f.release();await f.page.waitForFunction(()=>document.getElementById('auditStatus').textContent.startsWith('导出失败'));assert.equal(downloaded,false);assert.equal(await f.page.locator('#auditExport').isDisabled(),true);checks++;await f.context.close();
  console.log('PASS: '+checks+' audit UI checks (snapshot identity, read-only integration, coverage, evidence, filters, XSS, stale replies, exports, pagination and failures). No TIA calls.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
