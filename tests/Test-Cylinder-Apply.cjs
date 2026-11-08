const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright() {
  const locations=['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean);
  for(const location of locations){try{return require(location);}catch(error){if(error.code!=='MODULE_NOT_FOUND')throw error;}}
  throw new Error('Install Playwright or set PLAYWRIGHT_MODULE to run browser checks.');
}
const {chromium}=playwright();
const script=fs.readFileSync(path.join(__dirname,'../web/cylinder-apply.js'),'utf8');
const html='<div id="cyEditor"><input id="name"><button id="cyApply">生成并写入博途</button><button id="cyApplyPreview"></button></div><button id="cyCached"></button><button id="cyLive"></button><section id="cyApplyPanel" hidden><p id="cyApplyTarget"></p><p id="cyApplyStatus"></p><div id="cyApplySummary"></div><details id="cyApplyDetails"><pre id="cyApplyReport"></pre></details></section>';
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});let checks=0;
  async function fixture(mode) {
    const context=await browser.newContext(),page=await context.newPage(),requests=[];let reads=0,done=false;
    await page.route('http://localhost:18181/**',async route=>{
      const url=new URL(route.request().url());
      if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
      requests.push({method:route.request().method(),url:url.href,body:route.request().postDataJSON()});
      if(url.pathname.endsWith('/cylinder-apply')) {
        if(mode==='lost')return route.abort();
        if(mode==='rejected')return route.fulfill({status:409,json:{error:'所选点位已变化，尚未写入'}});
        return route.fulfill({status:202,json:{id:'0123456789abcdef0123456789abcdef'}});
      }
      reads++;
      const state=done||mode==='lost'||mode==='resume'?'completed':'running';
      return route.fulfill({json:{id:'0123456789abcdef0123456789abcdef',state,message:state==='completed'?'编译与回读通过，工程已保存':'正在检查占用',result:state==='completed'?{saved:true,import_result:{items:[{block_name:'TestCylinder',status:'imported',verification:'matched',backup:'E:\\test.xml'}]}}:null}});
    });
    await page.goto('http://localhost:18181/');
    await page.evaluate(mode=>{
      window.invalidated=0;window.cylinderSync=()=>{};
      window.cylinderIoInvalidate=()=>window.invalidated++;
      window.cylinderApplyRequest=()=>({id:'snapshot',host:'host',sample_id:'Z03',sample_network:3,source_hash:'hash',cylinder_name:'测试气缸',instance_name:'Z04',alarm_index:'4',rules:{i_Wpos:'input'},bindings:{i_Wpos:'free'},project_path:'E:\\test.ap17',plc_name:'PLC',target_token:'signed'});
      if(mode==='resume')localStorage.setItem('tia-cylinder-pending-write-v1',JSON.stringify({request_id:'01234567-89ab-cdef-0123-456789abcdef',id:'0123456789abcdef0123456789abcdef',target:'原工程',name:'测试气缸 / Z04'}));
    },mode);
    await page.addScriptTag({content:script});
    return{context,page,requests,setDone:()=>{done=true;},reads:()=>reads};
  }
  try{
    let f=await fixture('normal');
    await f.page.locator('#cyApply').click();await f.page.waitForFunction(()=>document.querySelector('#cyApplyStatus').textContent.includes('正在检查占用'));
    assert.equal(await f.page.locator('#name').isDisabled(),true);checks++;
    // A programmatic second click is ignored as well as the disabled UI button.
    await f.page.evaluate(()=>document.getElementById('cyApplyPreview').onclick());
    const writes=f.requests.filter(r=>r.method==='POST');assert.equal(writes.length,1);checks++;
    assert.equal(writes[0].body.source_hash,'hash');assert.deepEqual(writes[0].body.bindings,{i_Wpos:'free'});assert.equal(writes[0].body.target_token,'signed');assert.match(writes[0].body.request_id,/^[0-9a-f-]{36}$/);checks++;
    f.setDone();await f.page.waitForFunction(()=>document.querySelector('#cyApplyStatus').textContent.includes('生成并写入成功'));
    assert.equal(await f.page.evaluate(()=>window.invalidated),1);assert.equal(await f.page.locator('#name').isDisabled(),false);assert.match(await f.page.locator('#cyApplySummary').textContent(),/TestCylinder/);checks++;
    assert.equal(await f.page.evaluate(()=>localStorage.getItem('tia-cylinder-pending-write-v1')),null);checks++;await f.context.close();
    f=await fixture('lost');await f.page.locator('#cyApply').click();await f.page.waitForFunction(()=>document.querySelector('#cyApplyStatus').textContent.includes('生成并写入成功'));
    assert.equal(f.requests.filter(r=>r.method==='POST').length,1);assert.ok(f.requests.some(r=>r.method==='GET'&&r.url.includes('request_id=')));checks++;await f.context.close();
    f=await fixture('resume');await f.page.waitForFunction(()=>document.querySelector('#cyApplyStatus').textContent.includes('生成并写入成功'));
    assert.equal(f.requests.filter(r=>r.method==='POST').length,0);assert.match(await f.page.locator('#cyApplyTarget').textContent(),/原工程/);checks++;await f.context.close();
    f=await fixture('rejected');await f.page.locator('#cyApply').click();await f.page.waitForFunction(()=>document.querySelector('#cyApplyStatus').textContent.includes('尚未写入'));
    assert.equal(await f.page.evaluate(()=>window.invalidated),0);assert.equal(f.reads(),0);assert.equal(await f.page.locator('#cyApply').isDisabled(),false);checks++;await f.context.close();
    f=await fixture('normal');await f.page.evaluate(()=>window.cylinderApplyRequest=()=>null);await f.page.locator('#cyApply').click();assert.equal(f.requests.length,0);checks++;await f.context.close();
    console.log('PASS: '+checks+' cylinder write UI checks (one submit, locked editing, scoped request, saved result, lost-response recovery, reload recovery, rejection and validation). No TIA writes.');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
