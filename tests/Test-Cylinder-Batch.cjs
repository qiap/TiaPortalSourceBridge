// Real cylinder form, batch queue and apply flow, backed only by isolated HTTP fixtures.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const location of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(location);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw new Error('Install Playwright or set PLAYWRIGHT_MODULE.');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const scripts=['cylinder-model.js','cylinder-naming.js','cylinder.js','cylinder-batch.js','cylinder-apply.js'].map(file=>fs.readFileSync(path.join(root,'web',file),'utf8'));
const pins=['i_Wpos','i_Hpos','oOutput_W','oOutput_H'],rules=Object.fromEntries(pins.map((pin,i)=>[pin,i<2?'input':'output']));
const oldNames=['旧气缸伸出到位I1.0','旧气缸缩回到位I1.1','旧气缸伸出阀Q1.0','旧气缸缩回阀Q1.1'];
const xml=`<Document><SW.Blocks.FB><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static"><Member Name="Z01" Datatype="&quot;Cylinder.FB&quot;"/></Section></Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4"><Parts>${pins.map((pin,i)=>`<Access Scope="GlobalVariable" UId="${i+1}"><Symbol><Component Name="${oldNames[i]}"/></Symbol></Access>`).join('')}<Call UId="100"><CallInfo Name="Cylinder.FB" BlockType="FB"><Instance Scope="LocalVariable"><Component Name="Z01"/></Instance>${pins.map((pin,i)=>`<Parameter Name="${pin}" Section="${i<2?'Input':'Output'}" Type="Bool"/>`).join('')}</CallInfo></Call></Parts><Wires><Wire><Powerrail/><NameCon UId="100" Name="en"/></Wire>${pins.map((pin,i)=>`<Wire><IdentCon UId="${i+1}"/><NameCon UId="100" Name="${pin}"/></Wire>`).join('')}</Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>`;
const target={project_path:'E:\\Fixture\\Batch.ap17',plc_name:'Station/CPU',target_token:'fixture-only'};
const points=pins.map((pin,i)=>({id:'old'+i,area:i<2?'I':'Q',address:(i<2?'I':'Q')+'1.'+i%2,symbol:'"'+oldNames[i]+'"',availability:'used',bank:'station'}));
for(const area of ['I','Q'])for(let i=0;i<48;i++){const address=area+(2+Math.floor(i/8))+'.'+i%8;points.push({id:area+i,area,address,symbol:'"'+address+'"',availability:'unused',bank:'station'});}
(async()=>{
  const browser=await playwright().chromium.launch({channel:process.env.CYLINDER_TEST_BROWSER||'msedge',headless:true});let checks=0;
  const context=await browser.newContext(),page=await context.newPage(),writes=[],previews=[],errors=[];let done=false,delayPreview=false,releasePreview;
  try {
    await page.route('**/*',async route=>{
      const pathname=new URL(route.request().url()).pathname;
      if(pathname==='/')return route.fulfill({contentType:'text/html',body:html});
      if(pathname.endsWith('/cylinder-blocks'))return route.fulfill({json:{blocks:[{id:'host',name:'HostCylinder',group:'Cylinders'}]}});
      if(pathname.endsWith('/cylinder-template'))return route.fulfill({json:{xml,host:'HostCylinder',hash:'host-v1',project:target.project_path,plc:target.plc_name,unit:'',members:{},writes:{},readAt:'2026-01-01',points}});
      if(pathname.endsWith('/cylinder-templates'))return route.fulfill({json:{context:{sample_fingerprint:'a'.repeat(64)},matches:true,template:{schema:1,kind:'cylinder-template-rules',rules}}});
      if(pathname.endsWith('/cylinder-batch-preview')){
        const body=route.request().postDataJSON();previews.push(body);if(delayPreview)await new Promise(resolve=>releasePreview=resolve);
        return route.fulfill({json:{summary:{count:body.batch.length,blocks:3,note:'统一检查与编译',cylinders:body.batch.map(row=>({...row,renames:pins.map((pin,i)=>({pin,address:points.find(p=>p.id===row.bindings[pin]).address,new_symbol:row.cylinder_name+(i<2?'到位':'电磁阀')}))}))}}});
      }
      if(pathname.endsWith('/cylinder-apply')){writes.push(route.request().postDataJSON());return route.fulfill({status:202,json:{id:'a'.repeat(32)}});}
      if(pathname.endsWith('/cylinder-apply-status'))return route.fulfill({json:{id:'a'.repeat(32),state:done?'completed':'running',message:'正在核对',result:done?{saved:true}:null}});
      return route.fulfill({status:404,body:'mock route not found'});
    });
    page.on('pageerror',error=>errors.push(error.message));await page.goto('http://localhost:18182/');
    await page.evaluate(target=>{
      window.fixtureState={id:'snapshot',target,busy:false};window.cylinderIoState=()=>window.fixtureState;
      window.cylinderIoInvalidate=()=>{window.fixtureState.id='';window.cylinderSync();};
      document.querySelectorAll('.tab-content').forEach(e=>e.classList.remove('active'));document.getElementById('tab-cylinder').classList.add('active');
    },target);
    for(const content of scripts)await page.addScriptTag({content});
    await page.evaluate(()=>window.cylinderSync());await page.waitForFunction(()=>document.getElementById('cyHost').options.length===2);await page.locator('#cyHost').selectOption('host');
    await page.waitForFunction(()=>window.cylinderBatchContext().template_confirmed);
    async function fill(n,{name='气缸'+n,instance='Z'+String(n+2).padStart(2,'0'),index=String(n+2),offset=(n-1)*2}={}) {
      await page.locator('#cyDisplayName').fill(name);await page.locator('#cyName').fill(instance);
      await page.evaluate(index=>document.getElementById('cyAlarm').value=index,index);
      for(let i=0;i<4;i++)await page.locator('#cyPoints select[data-pin="'+pins[i]+'"]').selectOption((i<2?'I':'Q')+(offset+i%2));
    }
    async function add(n,opts){await fill(n,opts);await page.locator('#cyBatchAdd').click();}
    const count=()=>page.locator('#cyBatchRows tr').count().then(n=>Math.max(0,n-1));
    await add(1);assert.equal(await count(),1);assert.equal(await page.locator('#cyName').inputValue(),'');assert.equal(await page.locator('#cyDisplayName').inputValue(),'');assert.equal(await page.locator('#cyPoints select').first().inputValue(),'');checks++;
    assert.match(await page.locator('#cyBatchRows').textContent(),/I2\.0/);assert.match(await page.locator('#cyBatchRows').textContent(),/Q2\.1/);checks++;
    // The empty form after enqueue must never block batch submission.
    let request=await page.evaluate(()=>window.cylinderApplyRequest());assert.equal(request.batch.length,1);assert.deepEqual(Object.keys(request.batch[0]).sort(),['alarm_index','bindings','cylinder_name','instance_name']);assert.equal(request.id,'snapshot');assert.equal(request.target_token,target.target_token);checks++;
    await add(2,{name:'气缸1'});assert.equal(await count(),1);assert.match(await page.locator('#cyBatchStatus').textContent(),/相同气缸名称/);checks++;
    await add(2,{instance:'z03'});assert.equal(await count(),1);assert.match(await page.locator('#cyBatchStatus').textContent(),/相同程序实例/);checks++;
    await add(2,{index:'03'});assert.equal(await count(),1);assert.match(await page.locator('#cyBatchStatus').textContent(),/相同数组索引/);checks++;
    await add(2,{offset:0});assert.equal(await count(),1);assert.match(await page.locator('#cyBatchStatus').textContent(),/I2\.0 已被/);checks++;
    // Distinct symbols/IDs for the same physical address are also reserved.
    await page.evaluate(()=>{window.originalBatchContext=window.cylinderBatchContext;window.cylinderBatchContext=()=>{const c=window.originalBatchContext();c.points.find(p=>p.id==='I2').address='%I2.0';return c;};});
    await add(2);assert.equal(await count(),1);assert.match(await page.locator('#cyBatchStatus').textContent(),/I2\.0 已被/);checks++;
    await page.evaluate(()=>window.cylinderBatchContext=window.originalBatchContext);await add(2);assert.equal(await count(),2);checks++;
    await page.locator('#cyGenerate').click();await page.waitForFunction(()=>document.getElementById('cyBatchStatus').textContent.includes('批量方案检查通过'));
    assert.equal(previews.length,1);assert.equal(previews[0].batch.length,2);assert.equal(writes.length,0);assert.match(await page.locator('#cyDiff').textContent(),/气缸2电磁阀/);assert.equal(await page.locator('#cyDownload').isHidden(),true);checks++;
    await page.evaluate(()=>{window.cylinderApplying=true;window.cylinderBatchSync();document.getElementById('cyBatchAdd').onclick();document.querySelector('#cyBatchRows button').onclick();document.getElementById('cyBatchClear').onclick();});
    assert.equal(await count(),2);assert.equal(await page.evaluate(()=>window.cylinderApplyRequest()),null);assert.equal(await page.locator('#cyBatchClear').isDisabled(),true);checks++;
    await page.evaluate(()=>{window.cylinderApplying=false;window.cylinderBatchSync();});assert.match(await page.locator('#cyApply').textContent(),/2 个气缸/);checks++;
    await page.locator('#cyBatchRows button').first().click();assert.equal(await count(),1);request=await page.evaluate(()=>window.cylinderApplyRequest());assert.equal(request.batch[0].cylinder_name,'气缸2');checks++;
    delayPreview=true;await page.locator('#cyGenerate').click();while(!releasePreview)await new Promise(resolve=>setTimeout(resolve,5));
    // A changed template invalidates the complete queue, even if switched back later.
    await page.evaluate(()=>{window.cylinderBatchContext=()=>({...window.originalBatchContext(),sample_network:2});window.cylinderBatchSync();});
    releasePreview();await page.waitForFunction(()=>!document.getElementById('cyGenerate').disabled);assert.equal(await page.locator('#cyResult').isHidden(),true);delayPreview=false;checks++;
    assert.equal(await page.evaluate(()=>window.cylinderApplyRequest()),null);assert.match(await page.locator('#cyBatchStatus').textContent(),/已失效/);checks++;
    await page.evaluate(()=>window.cylinderBatchContext=window.originalBatchContext);assert.equal(await page.evaluate(()=>window.cylinderApplyRequest()),null);checks++;
    await page.locator('#cyBatchClear').click();await add(1);
    await page.evaluate(()=>{window.fixtureState.target={...window.fixtureState.target,target_token:'new-target'};window.cylinderBatchSync();});
    assert.equal(await page.evaluate(()=>window.cylinderApplyRequest()),null);checks++;
    await page.evaluate(target=>window.fixtureState.target=target,target);await page.locator('#cyBatchClear').click();
    for(let i=1;i<=20;i++)await add(i);
    assert.equal(await count(),20);assert.equal(await page.locator('#cyBatchAdd').isDisabled(),true);await page.evaluate(()=>document.getElementById('cyBatchAdd').onclick());assert.equal(await count(),20);checks++;
    await page.locator('#cyBatchClear').click();await add(1);await add(2);
    await page.locator('#cyApply').click();await page.waitForFunction(()=>document.getElementById('cyApplyStatus').textContent.includes('正在核对'));
    assert.equal(writes.length,1);assert.equal(writes[0].batch.length,2);assert.equal(writes[0].batch[1].bindings.oOutput_H,'Q3');assert.deepEqual(writes[0].rules,rules);assert.match(await page.locator('#cyApplyTarget').textContent(),/批量 2 个气缸/);checks++;
    assert.equal(await page.locator('#cyBatchAdd').isDisabled(),true);await page.evaluate(()=>document.getElementById('cyBatchClear').onclick());assert.equal(await count(),2);checks++;
    done=true;await page.waitForFunction(()=>document.getElementById('cyApplyStatus').textContent.includes('生成并写入成功'));
    assert.match(await page.locator('#cyApply').textContent(),/2 个气缸/);assert.equal(await page.evaluate(()=>window.cylinderApplyRequest()),null);assert.equal(writes.length,1);checks++;
    assert.deepEqual(errors,[]);checks++;
    console.log('PASS: '+checks+' batch browser checks (real form + queue + apply, 20-row limit, physical/name/index conflicts, empty-form submit, invalidation, busy protection and no replay). No TIA calls.');
  } finally {await context.close();await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

