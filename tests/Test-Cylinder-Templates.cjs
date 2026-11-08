// Real browser DOM and isolated HTTP mocks. This never contacts the dashboard or TIA.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const location of ['playwright',process.env.PLAYWRIGHT_MODULE,process.env.USERPROFILE&&path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(location);}catch(error){if(error.code!=='MODULE_NOT_FOUND')throw error;}}throw new Error('Install Playwright or set PLAYWRIGHT_MODULE.');}
const root=path.join(__dirname,'..');
const html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const scripts=['cylinder-model.js','cylinder-naming.js','cylinder.js'].map(file=>fs.readFileSync(path.join(root,'web',file),'utf8'));
const network=(id='Z01')=>`<SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4"><Parts><Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="旧气缸伸出位I1.0"/></Symbol></Access><Access Scope="GlobalVariable" UId="2"><Symbol><Component Name="旧气缸伸出阀Q1.0"/></Symbol></Access><Call UId="100"><CallInfo Name="Cylinder.FB" BlockType="FB"><Instance Scope="LocalVariable"><Component Name="${id}"/></Instance><Parameter Name="i_Wpos" Section="Input" Type="Bool"/><Parameter Name="oOutput_W" Section="Output" Type="Bool"/></CallInfo></Call></Parts><Wires><Wire><Powerrail/><NameCon UId="100" Name="en"/></Wire><Wire><IdentCon UId="1"/><NameCon UId="100" Name="i_Wpos"/></Wire><Wire><IdentCon UId="2"/><NameCon UId="100" Name="oOutput_W"/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>`;
const xml=extra=>`<Document><SW.Blocks.FB><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static"><Member Name="Z01" Datatype="&quot;Cylinder.FB&quot;"/><Member Name="Z02" Datatype="&quot;Cylinder.FB&quot;"/></Section></Sections></Interface></AttributeList><ObjectList>${network()}${extra?network('Z02'):''}</ObjectList></SW.Blocks.FB></Document>`;
const point=(id,area,address,symbol,availability)=>({id,area,address,symbol,availability,bank:'station'});
const rules={i_Wpos:'input',oOutput_W:'output'},target={project_path:'E:\\Fixture\\Project.ap17',plc_name:'Station/CPU',target_token:'fixture-only'};
const legacyTarget=JSON.stringify(['cylinder-v1',target.project_path.toLowerCase(),target.plc_name]);
const legacyKey=JSON.stringify([legacyTarget,'host','host-v1','Z01',1]);
(async()=>{
  const browser=await playwright().chromium.launch({channel:process.env.CYLINDER_TEST_BROWSER||'msedge',headless:true});let checks=0;
  const store={record:null,fingerprint:'a'.repeat(64)};
  async function fixture({extra=false,legacy=false,staleLegacy=false,delay=false}={}){
    const context=await browser.newContext(),page=await context.newPage(),writes=[],errors=[];let release;
    const metadata={project_path:target.project_path,plc_name:target.plc_name,host_id:'host',host_name:'HostCylinder',group_path:'Cylinders',unit_name:'',sample_id:'Z01',sample_network:1,fb:'Cylinder.FB',parameters:Object.keys(rules),sample_fingerprint:store.fingerprint};
    await page.route('**/*',async route=>{
      const url=new URL(route.request().url());
      if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
      if(url.pathname.endsWith('/cylinder-blocks'))return route.fulfill({json:{blocks:[{id:'host',name:'HostCylinder',group:'Cylinders'}]}});
      if(url.pathname.endsWith('/cylinder-template'))return route.fulfill({json:{xml:xml(extra),host:'HostCylinder',hash:extra?'host-v2':'host-v1',project:target.project_path,plc:target.plc_name,unit:'',members:{},writes:{},readAt:'2026-01-01',points:[point('oldi','I','I1.0','"旧气缸伸出位I1.0"','used'),point('oldq','Q','Q1.0','"旧气缸伸出阀Q1.0"','used'),point('newi','I','I1.1','"I1.1"','unused'),point('newq','Q','Q1.1','"Q1.1"','unused')]}});
      if(url.pathname.endsWith('/cylinder-templates')){
        if(route.request().method()==='POST'){
          const body=route.request().postDataJSON();writes.push(body);
          if(delay)await new Promise(resolve=>{release=resolve;});
          assert.equal(body.reviewed,true);assert.equal(body.sample_fingerprint,metadata.sample_fingerprint);
          store.record={schema:1,kind:'cylinder-template-rules',...metadata,rules:body.rules,updated:'2026-01-01'};
        }
        return route.fulfill({json:{context:metadata,template:store.record,matches:!!store.record&&store.record.sample_fingerprint===metadata.sample_fingerprint}});
      }
      return route.fulfill({status:404,body:'mock route not found'});
    });
    page.on('pageerror',error=>errors.push(error.message));
    await page.goto('http://cylinder-templates.test/');
    await page.evaluate(({target,legacy,staleLegacy,legacyKey,legacyTarget,rules})=>{
      window.cylinderIoState=()=>({id:'snapshot',target,busy:false});
      document.querySelectorAll('.tab-content').forEach(x=>x.classList.remove('active'));
      document.getElementById('tab-cylinder').classList.add('active');
      if(legacy){localStorage.setItem(staleLegacy?legacyKey.replace('host-v1','older-host'):legacyKey,JSON.stringify(rules));localStorage.setItem(legacyTarget,JSON.stringify({host:'host',id:'Z01',network:1}));}
    },{target,legacy,staleLegacy,legacyKey,legacyTarget,rules});
    for(const content of scripts)await page.addScriptTag({content});
    await page.evaluate(()=>window.cylinderSync());
    if(!legacy){await page.waitForFunction(()=>document.getElementById('cyHost').options.length===2);await page.locator('#cyHost').selectOption('host');}
    await page.waitForFunction(()=>!document.getElementById('cySave').disabled);
    return {context,page,writes,errors,release:()=>release()};
  }
  async function save(f){await f.page.locator('#cySave').click();await f.page.waitForFunction(()=>window.cylinderBatchContext().template_confirmed);}
  try{
    let f=await fixture();
    assert.match(await f.page.locator('#cyTemplateStatus').textContent(),/尚无项目模板/);
    assert.equal(await f.page.locator('#cyBinding').isHidden(),true);checks++;
    await save(f);assert.deepEqual(f.writes[0].rules,rules);assert.equal(await f.page.locator('#cyBinding').isHidden(),false);checks++;
    const downloadPromise=f.page.waitForEvent('download');await f.page.locator('#cyTemplateExport').click();const download=await downloadPromise;
    const exported=JSON.parse(fs.readFileSync(await download.path(),'utf8'));
    assert.equal(exported.kind,'cylinder-template-rules');assert.deepEqual(exported.rules,rules);assert.equal(exported.call,undefined);assert.equal(exported.bindings,undefined);checks++;
    assert.deepEqual(f.errors,[]);await f.context.close();
    f=await fixture({extra:true});
    assert.equal(await f.page.evaluate(()=>window.cylinderBatchContext().template_confirmed),true);assert.equal(f.writes.length,0);assert.match(await f.page.locator('#cyTemplateStatus').textContent(),/核对一致/);checks++;
    assert.equal(await f.page.evaluate(()=>localStorage.length),0,'load works with completely new browser storage');checks++;
    await f.context.close();
    store.fingerprint='b'.repeat(64);f=await fixture();
    assert.match(await f.page.locator('#cyTemplateStatus').textContent(),/已变化/);assert.equal(await f.page.locator('#cyBinding').isHidden(),true);assert.equal(await f.page.evaluate(()=>window.cylinderApplyRequest()),null);checks++;
    await save(f);assert.equal(store.record.sample_fingerprint,store.fingerprint);checks++;
    const foreign={...exported,project_path:'E:\\OtherProject.ap17',host_name:'ForeignHost',bindings:{i_Wpos:'foreign-point'},rules};
    await f.page.locator('#cyTemplateFile').setInputFiles({name:'foreign-template.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(foreign))});
    await f.page.waitForFunction(()=>document.getElementById('cyTemplateStatus').textContent.includes('ForeignHost'));
    assert.equal(f.writes.length,1,'import does not save automatically');assert.equal(await f.page.locator('#cyBinding').isHidden(),true);assert.equal(await f.page.evaluate(()=>window.cylinderApplyRequest()),null);assert.doesNotMatch(await f.page.locator('#cyRules').textContent(),/foreign-point/);checks++;
    await save(f);assert.equal(f.writes.length,2);assert.equal(f.writes[1].bindings,undefined);assert.equal(store.record.project_path,target.project_path);checks++;
    await f.page.locator('#cyTemplateFile').setInputFiles({name:'draft.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({schema:1,kind:'cylinder-draft',rules}))});
    await f.page.waitForFunction(()=>document.getElementById('cyTemplateStatus').textContent.includes('不能作为模板'));
    assert.equal(f.writes.length,2);checks++;
    assert.deepEqual(f.errors,[]);await f.context.close();
    store.record=null;f=await fixture({legacy:true});
    assert.match(await f.page.locator('#cyTemplateStatus').textContent(),/浏览器旧规则/);assert.equal(await f.page.locator('#cyBinding').isHidden(),true);
    await save(f);assert.equal(await f.page.evaluate(key=>localStorage.getItem(key),legacyKey),null);checks++;await f.context.close();
    store.record=null;f=await fixture({legacy:true,staleLegacy:true});
    assert.match(await f.page.locator('#cyTemplateStatus').textContent(),/尚无项目模板/);assert.equal(f.writes.length,0);checks++;await f.context.close();
    f=await fixture({delay:true});await f.page.locator('#cySave').click();await f.page.waitForFunction(()=>document.getElementById('cyTemplateStatus').textContent.includes('正在保存'));
    await f.page.locator('[data-template-pin="i_Wpos"]').selectOption('shared');
    while(!f.writes.length)await new Promise(resolve=>setTimeout(resolve,10));f.release();
    await f.page.waitForFunction(()=>document.getElementById('cyTemplateStatus').textContent.includes('随后又发生变化'));
    assert.equal(await f.page.evaluate(()=>window.cylinderBatchContext().template_confirmed),false);assert.equal(await f.page.locator('#cyBinding').isHidden(),true);checks++;
    assert.deepEqual(f.errors,[]);await f.context.close();
    console.log('PASS: '+checks+' browser template checks (persistence, semantic invalidation, import review, export, legacy migration and concurrent edit). No TIA calls.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
