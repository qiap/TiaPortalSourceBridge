const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
function playwright(){for(const p of ['playwright',process.env.PLAYWRIGHT_MODULE,path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean)){try{return require(p);}catch(e){if(e.code!=='MODULE_NOT_FOUND')throw e;}}throw Error('Playwright required');}
const root=path.join(__dirname,'..'),html=fs.readFileSync(path.join(root,'web/index.html'),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,''),script=fs.readFileSync(path.join(root,'web/io-tag-review.js'),'utf8');
(async()=>{
 const browser=await playwright().chromium.launch({channel:'msedge',headless:true});let checks=0;
 async function fixture(mode='normal'){
  const context=await browser.newContext(),page=await context.newPage(),calls=[],errors=[];let release;
  await page.route('**/*',async route=>{
   const url=new URL(route.request().url());if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:html});
   const action=url.pathname.split('/').pop(),data=route.request().method()==='POST'?route.request().postDataJSON():Object.fromEntries(url.searchParams);calls.push({action,data});
   if(action==='preview'||action==='restore-preview'){
    if(mode==='delay')await new Promise(r=>release=r);
    return route.fulfill({json:{id:'plan',table_name:'Tags <img>',warning:'保存整个工程',diff:{status:'structural',changes:Array.from({length:mode==='many'?101:1},()=>({path:'Tag <img>',type:'change',before:'old',after:'<script>new</script>'}))},recovery_of:action==='restore-preview'?'old':''}});
   }
   if(action==='apply')return mode==='lost'?route.fulfill({status:500,json:{error:'连接中断'}}):route.fulfill({status:202,json:{id:'plan',state:'accepted'}});
   if(action==='status')return route.fulfill({json:{id:'plan',state:'completed',table_name:'Tags',saved:true}});
   if(action==='history')return route.fulfill({json:[{id:'old',state:'needs_review',table_name:'Tags',backup:'backup.xml',error:'导入失败 <img>'}]});
   return route.fulfill({status:404,json:{error:'unexpected'}});
  });
  page.on('pageerror',e=>errors.push(e.message));await page.goto('http://localhost/');await page.evaluate(()=>{
   window.reviewState={valid:true,busy:false,target:{project_path:'E:/Fixture.ap17',plc_name:'PLC',target_token:'token'},report:{id:'report',options:{scope:'native',unit:'',table:'\nTags'}}};window.ioReconcileReview=()=>reviewState;
   document.getElementById('tab-io').classList.add('active');document.getElementById('recReport').hidden=false;
  });await page.addScriptTag({content:script});await page.evaluate(()=>{
   for(const row of [{excel_row:2,status:'different'},{excel_row:3,status:'excel_only'},{excel_row:4,status:'conflict'}]){const tr=document.createElement('tr'),td=document.createElement('td');tr.append(td);document.getElementById('recRows').append(tr);tagReviewRow(row,td);}
  });return{context,page,calls,errors,pending:()=>!!release,release:()=>release?.()};
 }
 async function preview(f){await f.page.getByLabel('选择 Excel 第 2 行').check();await f.page.locator('#tagReviewPreview').click();await f.page.waitForFunction(()=>document.getElementById('tagReviewPlan').textContent.includes('写回预览'));}
 try{
  let f=await fixture();assert.equal(await f.page.locator('[data-tag-review-row]').count(),2);assert.equal(await f.page.locator('#tagReviewPreview').isDisabled(),true);checks++;
  await preview(f);assert.deepEqual(f.calls.find(c=>c.action==='preview').data.rows,[2]);assert.equal(f.calls[0].data.target_token,'token');checks++;
  assert.equal(await f.page.locator('#tagReviewApply').isDisabled(),true);assert.equal(await f.page.locator('#tagReviewPlan img,#tagReviewPlan script').count(),0);checks++;
  await f.page.locator('#tagReviewConfirm').check();await f.page.locator('#tagReviewApply').click();await f.page.waitForFunction(()=>document.getElementById('tagReviewStatus').textContent.includes('保存完成'));assert.equal(f.calls.filter(c=>c.action==='apply').length,1);assert.equal(await f.page.locator('#tagReviewApply').isDisabled(),true);checks++;
  await f.page.locator('#tagReviewHistory').click();await f.page.waitForFunction(()=>document.getElementById('tagReviewRecords').textContent.includes('需要核对'));assert.equal(await f.page.locator('#tagReviewRecords img').count(),0);await f.page.locator('#tagReviewRecords summary').click();assert.equal(await f.page.getByRole('button',{name:'核对现场并预览恢复'}).isEnabled(),true);checks++;
  await f.page.getByRole('button',{name:'核对现场并预览恢复'}).click();await f.page.waitForFunction(()=>document.getElementById('tagReviewPlan').textContent.includes('恢复备份'));assert.equal(await f.page.locator('#tagReviewConfirm').isChecked(),false);assert.deepEqual(f.errors,[]);checks++;await f.context.close();
  f=await fixture('many');await preview(f);await f.page.locator('#tagReviewConfirm').isDisabled().then(v=>assert.equal(v,true));checks++;await f.context.close();
  f=await fixture('lost');await preview(f);await f.page.locator('#tagReviewConfirm').check();await f.page.locator('#tagReviewApply').click();await f.page.waitForFunction(()=>document.getElementById('tagReviewStatus').textContent.includes('连接中断'));assert.equal(await f.page.locator('#tagReviewApply').isDisabled(),true);assert.equal(f.calls.filter(c=>c.action==='apply').length,1);checks++;await f.context.close();
  f=await fixture('delay');await f.page.getByLabel('选择 Excel 第 2 行').check();await f.page.locator('#tagReviewPreview').click();while(!f.pending())await new Promise(r=>setTimeout(r,10));await f.page.evaluate(()=>{reviewState.target.plc_name='Other';tagReviewReset();});f.release();await f.page.waitForFunction(()=>!document.getElementById('tagReviewHistory').disabled);assert.equal(await f.page.locator('#tagReviewPlan').textContent(),'');checks++;await f.context.close();
  f=await fixture();await f.page.evaluate(()=>reviewState.target.project_path='E:/Fixture.ap21');await f.page.waitForTimeout(800);assert.equal(await f.page.locator('#tagReviewPreview').isDisabled(),true);assert.equal(await f.page.locator('#tagReviewHistory').isDisabled(),true);checks++;await f.context.close();
  console.log('PASS: '+checks+' reviewed tag write UI checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
