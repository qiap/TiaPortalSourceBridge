const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../web/index.html'),'utf8');
const code=html.slice(html.indexOf('    function setIndexScope('),html.indexOf('    async function loadIndexSummary('));
const elements={indexScopeQuick:{value:'PLC'},indexScopeDetail:{value:'PLC'}};
const requests=[],confirmations=[];
const context={document:{getElementById:id=>elements[id]},currentStatus:{active_project:'fixture.ap17',target_plc:'CPU',target_token:'snapshot'},confirm:text=>{confirmations.push(text);return true;},alert:text=>{throw Error(text)},appendConsoleLog(){},fetchStatus(){},fetch:async(url,request)=>{requests.push(JSON.parse(request.body));return {json:async()=>({success:true})}},console};
vm.createContext(context);vm.runInContext(code,context);
(async()=>{
 for(const scope of ['PLC','HMI','Hardware','All']){
  context.setIndexScope(scope);assert.equal(elements.indexScopeDetail.value,scope);
  await context.runAction('build-index','建立索引');
  assert.equal(requests.at(-1).index_scope,scope);assert.equal(requests.at(-1).project_path,'fixture.ap17');
 }
 assert.match(confirmations[2],/硬件/);assert.match(confirmations[3],/全部/);
 await context.runAction('compile','编译');assert.equal(requests.at(-1).index_scope,undefined);
 console.log('PASS: four UI scope requests, synchronized selectors, target preservation and non-index actions');
})().catch(e=>{console.error(e);process.exitCode=1;});
