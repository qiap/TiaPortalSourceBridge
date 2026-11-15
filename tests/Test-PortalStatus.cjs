const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8');
let checks=0;const ctx=vm.createContext({Date,Number});
vm.runInContext(html.slice(html.indexOf('    function portalDiscoveryPresentation('),html.indexOf('    async function fetchStatus(')),ctx);
const now=Date.now(),state=(kind,age,error='')=>({state:kind,snapshot_utc:age===null?'':new Date(now-age).toISOString(),error});
for(const [discovery,expected] of [[undefined,'ready'],[state('pending',null),'checking'],[state('pending',2000),'refreshing'],[state('ready',2000),'ready'],[state('pending',31000),'stale'],[state('ready',31000),'stale'],[state('failed',2000,'read failed'),'failed'],[state('pending',2000,'last failure'),'failed'],[state('pending',-6000),'stale'],[{state:'pending',snapshot_utc:'bad'},'checking']]){assert.equal(ctx.portalDiscoveryPresentation(discovery,now).kind,expected);checks++;}
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
(async()=>{const browser=await playwright().chromium.launch({channel:'msedge',headless:true});try{
  const page=await browser.newPage(),errors=[];let discovery={state:'pending',snapshot_utc:'',error:''},running=true,project='A';
  await page.addInitScript(()=>{window.setInterval=()=>0;});
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',r=>{const route=new URL(r.request().url()).pathname;
    if(route==='/'||/.(js|css|ico)$/.test(route)){const file=path.join(root,'web',route==='/'?'index.html':path.basename(route));return r.fulfill({contentType:route.endsWith('.js')?'text/javascript':route.endsWith('.css')?'text/css':'text/html',body:fs.readFileSync(file)});}
    if(route==='/api/status')return r.fulfill({json:{active_project:'E:/'+project+'.ap17',active_project_name:project,project_path:'E:/'+project+'.ap17',target_plc:'PLC',plc_name:'PLC',target_token:'test',tool_version:'v4.13.0',active_project_is_running:running,active_project_pid:running?123:0,available_projects:[],tia_processes:running?[{pid:123,memory_mb:100}]:[],portal_discovery:discovery}});
    return r.fulfill({json:{status:'idle',items:[],tasks:[],jobs:[],logs:[],tools:[]}});
  });
  await page.goto('http://localhost/');await page.waitForFunction(()=>window.currentStatus?.target_token==='test');
  const badge=page.locator('#currentProjectBadge'),tia=page.locator('#tiaStatusBadge'),dot=page.locator('#connectionDot');
  assert.equal(await badge.textContent(),'正在检测博途…');assert.equal(await tia.textContent(),'检测中');checks++;
  async function refresh(next){discovery=next;await page.evaluate(()=>fetchStatus(true));}
  const fresh=()=>new Date().toISOString();
  await refresh({state:'ready',snapshot_utc:fresh(),error:''});
  const stable={badge:await badge.textContent(),tia:await tia.textContent(),dot:await dot.getAttribute('class')};
  assert.match(stable.badge,/博途在线/);checks++;
  for(let i=0;i<4;i++){await refresh({state:'pending',snapshot_utc:fresh(),error:'',current_verified:false});
    assert.equal(await badge.textContent(),stable.badge);assert.equal(await tia.textContent(),stable.tia);assert.equal(await dot.getAttribute('class'),stable.dot);assert.match(await badge.getAttribute('title'),/后台更新/);checks++;
    await refresh({state:'ready',snapshot_utc:fresh(),error:''});assert.equal(await badge.textContent(),stable.badge);checks++;}
  project='B';running=false;await refresh({state:'pending',snapshot_utc:fresh(),error:''});
  assert.equal(await badge.textContent(),'本地工程');assert.equal(await page.locator('#currentProjectText').textContent(),'B');checks++;
  running=true;await refresh({state:'pending',snapshot_utc:new Date(Date.now()-31000).toISOString(),error:''});
  assert.equal(await badge.textContent(),'状态待确认');assert.equal(await tia.textContent(),'状态待确认');assert.ok(!(await dot.getAttribute('class')).split(' ').includes('online'));checks++;
  await refresh({state:'failed',snapshot_utc:fresh(),error:'Failed <img src=x onerror=alert(1)>'});
  assert.match(await badge.textContent(),/检测失败/);assert.equal(await page.locator('#tiaProcessDesc img').count(),0);checks++;
  await refresh({state:'pending',snapshot_utc:fresh(),error:'Previous failure'});assert.match(await badge.textContent(),/检测失败/);checks++;
  running=false;await refresh({state:'ready',snapshot_utc:fresh(),error:''});assert.equal(await badge.textContent(),'本地工程');assert.equal(await tia.textContent(),'未检测到博途');checks++;
  assert.deepEqual(errors,[]);checks++;
  console.log('PASS: '+checks+' portal status checks: cold start, stable background refresh, failures, stale snapshots and project changes.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
