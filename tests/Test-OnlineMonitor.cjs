const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,''),script=fs.readFileSync(path.join(root,'web/online-monitor.js'),'utf8');
(async()=>{const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=0;
 try{const context=await browser.newContext(),page=await context.newPage(),calls=[],errors=[];
  const sample={complete:true,values:{'<img>':{value:42,quality:'good',timestamp_utc:'2026-11-08T00:00:00Z',address:'DB10.DBW0'},Bad:{value:null,quality:'bad',error:'No access'}}};
  await page.route('**/*',route=>{const url=new URL(route.request().url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});const action=url.pathname.split('/').pop(),data=route.request().postDataJSON();calls.push({action,data});
   if(action==='connect')return route.fulfill({json:{session_id:'session',read_only:true}});
   if(action==='read')return route.fulfill({json:sample});if(action==='snapshot')return route.fulfill({json:{id:'archive',sample,note:'non atomic'}});
   if(action==='events')return route.fulfill({json:{watch:{running:false,samples:2,events:2},events:[{values:{'<img>':{value:false,quality:'good',timestamp_utc:'2026-11-08T00:00:00Z'}}},{values:{'<img>':{value:true,quality:'good',timestamp_utc:'2026-11-08T00:00:01Z'}}}]}});
   return route.fulfill({json:{}});
  });page.on('pageerror',e=>errors.push(e.message));await page.goto('http://localhost/');await page.evaluate(()=>{window.currentStatus={active_project:'E:/Fixture.ap17',target_plc:'PLC',target_token:'token'};document.getElementById('tab-monitor').classList.add('active');});await page.addScriptTag({content:script});
  assert.equal(await page.locator('#monitorRead').isDisabled(),true);checks++;
  await page.locator('#monitorIp').fill('127.0.0.1');await page.locator('#monitorTags').fill('DB_Process.Value | DB10.DBW0 | Int');await page.locator('#monitorConnect').click();await page.waitForFunction(()=>!document.getElementById('monitorRead').disabled);
  const request=calls.find(c=>c.action==='connect').data;assert.equal(request.profile.Tags[0].TestWritable,false);assert.equal(request.target_token,'token');assert.equal(request.profile.Ip,'127.0.0.1');checks++;
  await page.locator('#monitorRead').click();await page.waitForFunction(()=>document.getElementById('monitorValues').textContent.includes('42'));assert.match(await page.locator('#monitorValues').textContent(),/未知/);assert.equal(await page.locator('#monitorValues img').count(),0);checks++;
  await page.locator('#monitorSnapshot').click();await page.waitForFunction(()=>!document.getElementById('monitorDownloadSnapshot').disabled);assert.match(await page.locator('#monitorArchive').textContent(),/archive/);checks++;
  await page.locator('#monitorStart').click();await page.waitForFunction(()=>document.querySelector('#monitorWaveforms svg'));assert.equal(await page.locator('#monitorWaveforms img').count(),0);assert.match(await page.locator('#monitorTraceStatus').textContent(),/采样 2 次/);checks++;
  await page.evaluate(()=>currentStatus.target_plc='Other');await page.waitForFunction(()=>document.getElementById('monitorRead').disabled);assert.equal(await page.locator('#monitorDisconnect').isEnabled(),true);assert.equal(await page.locator('#monitorValues').textContent(),'');assert.equal(await page.locator('#monitorDownloadSnapshot').isDisabled(),true);checks++;
  await page.locator('#monitorDisconnect').click();await page.waitForFunction(()=>!document.getElementById('monitorConnect').disabled);assert.equal(calls.find(c=>c.action==='disconnect').data.plc_name,'PLC');assert.equal(calls.find(c=>c.action==='disconnect').data.session_id,'session');checks++;
  assert.deepEqual(errors,[]);assert.ok(!calls.some(c=>/write|download|force/.test(c.action)));checks++;await context.close();console.log('PASS: '+checks+' read-only monitor UI checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
