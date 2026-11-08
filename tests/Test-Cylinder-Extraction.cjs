// Uses the browser's real XML DOM. No TIA installation or engineering project required.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

function playwright() {
  const locations = ['playwright', process.env.PLAYWRIGHT_MODULE,
    process.env.USERPROFILE && path.join(process.env.USERPROFILE,
      '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')].filter(Boolean);
  for (const location of locations) {
    try { return require(location); } catch (error) { if (error.code !== 'MODULE_NOT_FOUND') throw error; }
  }
  throw new Error('Install Playwright or set PLAYWRIGHT_MODULE to run the browser XML regression.');
}

const parameter = (name, section) => `<Parameter Name="${name}" Section="${section}" Type="Bool"/>`;
const access = (id, name) => `<Access Scope="GlobalVariable" UId="${id}"><Symbol><Component Name="${name}"/></Symbol></Access>`;
const wire = (pin, connection, call = 100) => `<Wire>${connection}<NameCon UId="${call}" Name="${pin}"/></Wire>`;
const call = (id = 'Z01', uid = 100, extraParameter = '') => `<Call UId="${uid}"><CallInfo Name="Cylinder.FB" BlockType="FB"><Instance Scope="LocalVariable"><Component Name="${id}"/></Instance>${parameter('Sense','Input')}${parameter('Valve','Output')}${parameter('Home','Input')}${parameter('HomeValve','Output')}${extraParameter}</CallInfo></Call>`;
const wires = () => [wire('en','<Powerrail/>'), wire('Sense','<IdentCon UId="1"/>'),
  wire('Valve','<IdentCon UId="2"/>'), wire('Home','<OpenCon UId="20"/>'), wire('HomeValve','<OpenCon UId="21"/>')];
const network = (extraParts = '', connections = wires(), callXml = call()) => `<SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4"><Parts>${access(1,'OldSense')}${access(2,'OldValve')}${callXml}${extraParts}</Parts><Wires>${connections.join('')}</Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>`;
const xml = (...networks) => `<?xml version="1.0"?><Document><SW.Blocks.FB><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static"><Member Name="Z01" Datatype="&quot;Cylinder.FB&quot;"/><Member Name="Z02" Datatype="&quot;Cylinder.FB&quot;"/></Section></Sections></Interface></AttributeList><ObjectList>${networks.join('')}</ObjectList></SW.Blocks.FB></Document>`;
const fixtures = {
  open: xml(network()),
  missing: xml(network('', wires().filter(w => !w.includes('Name="Home"')))),
  inout: xml(network('', [...wires(), wire('State','<OpenCon UId="22"/>')], call('Z01',100,parameter('State','InOut')))),
  logic: xml(network('<Part Name="Contact" UId="30"/>')),
  conditional: xml(network(access(3,'Enable'), wires().map(w => w.replace('<Powerrail/>','<IdentCon UId="3"/>')))),
  duplicate: xml(network('', [...wires(), wire('Sense','<IdentCon UId="1"/>')])),
  branched: xml(network('', wires().map(w => w.includes('Name="Sense"') ? w.replace('</Wire>','<NameCon UId="100" Name="Home"/></Wire>') : w))),
  multi: xml(network(call('Z02',101))),
  mixed: xml(network(), network('<Part Name="Contact" UId="30"/>',wires(),call('Z02'))),
  eno: xml(network('', [...wires(),wire('eno','<OpenCon UId="23"/>')])),
  feedback: xml(network().replace('Name="OldSense"','Name="OldValve"')),
  endpointLogic: xml(network(), network('<Part Name="Contact" UId="30"/>',[
    ...wires().map(w=>w.includes('Name="Sense"')?wire('Sense','<NameCon UId="30" Name="out"/>'):w),
    '<Wire><IdentCon UId="1"/><NameCon UId="30" Name="operand"/></Wire>',
    '<Wire><Powerrail/><NameCon UId="30" Name="in"/></Wire>'
  ],call('Z02',100,parameter('i_Interlock_Wpos','Input'))).replace('Name="OldSense"','Name="LastSense"')),
};

(async () => {
  const { chromium } = playwright();
  const browser = await chromium.launch({channel:process.env.CYLINDER_TEST_BROWSER || 'msedge',headless:true});
  try {
    const page = await browser.newPage();
    await page.addScriptTag({content:fs.readFileSync(path.join(__dirname,'../web/cylinder-model.js'),'utf8')});
    const result = await page.evaluate(fixtures => {
      const api = window.CylinderModel;
      const models = Object.fromEntries(Object.entries(fixtures).map(([name, xml]) => [name,api.extract({xml})]));
      const model = models.open, sample = model.samples[0];
      const data = {members:{},points:[
        {id:'i',symbol:'"NewSense"',area:'I',address:'%I0.0',availability:'unused',writes:[]},
        {id:'q',symbol:'"NewValve"',area:'Q',address:'%Q0.0',availability:'unused',writes:[]}
      ],writes:{}};
      const rules = {Sense:'input',Valve:'output',Home:'open',HomeValve:'open'};
      const generate = (s = sample, r = rules) => api.generate(data, model, s, r, 'Z09', {Sense:'i',Valve:'q'}, '9');
      const message = run => { try {run();return '';} catch (e) {return e.message;} };
      const feedback = models.feedback.samples[0];
      const feedbackRules = {...rules,Sense:'feedback'};
      const endpoints={points:[
        {id:'old',symbol:'"OldSense"',area:'I',address:'I52.0',availability:'used',bank:'275'},
        {id:'last',symbol:'"LastSense"',area:'I',address:'I52.7',availability:'used',bank:'275'},
        {id:'before',symbol:'"Before"',area:'I',address:'I52.1',availability:'unused',bank:'275'},
        {id:'after',symbol:'"After"',area:'I',address:'I53.0',availability:'unused',bank:'275'}
      ]};
      return {
        models:Object.fromEntries(Object.entries(models).map(([name,m]) => [name,{
          samples:m.samples.map(s=>s.id),candidates:m.candidates,
          instances:[...m.instances],occupied:[...m.occupied],hostSymbols:[...m.hostSymbols],endpointSymbols:m.endpointSymbols
        }])),
        roles:sample.pins.map(p=>({name:p.name,roles:api.roles(p,sample)})),
        generated:generate(),
        feedbackRoles:api.roles(feedback.pins.find(p=>p.name==='Sense'),feedback),
        feedback:api.generate(data,models.feedback,feedback,feedbackRules,'Z09',{Valve:'q'},'9'),
        cannotKeepDonorFeedback:message(()=>api.generate(data,models.feedback,feedback,{...feedbackRules,Sense:'shared'},'Z09',{Valve:'q'},'9')),
        feedbackMissingOutput:message(()=>api.generate(data,models.feedback,feedback,feedbackRules,'Z09',{},'9')),
        feedbackWrongOutput:message(()=>api.generate(data,models.feedback,feedback,feedbackRules,'Z09',{Valve:'i'},'9')),
        endpoints:api.availablePoints(endpoints,models.endpointLogic,models.endpointLogic.samples[0],'I'),
        cannotDropConnected:message(()=>generate(sample,{...rules,Sense:'open'})),
        cannotGenerateUnsupported:message(()=>generate(models.logic.candidates[0])),
        malformed:message(()=>api.extract({xml:'<Document><broken></Document>'}))
      };
    }, fixtures);
    assert.deepEqual(result.models.open.samples,['Z01']);
    assert.equal(result.models.open.candidates.length,1);
    assert.equal(result.models.open.candidates[0].supported,true);
    for (const pin of ['Home','HomeValve']) {
      assert.deepEqual(result.roles.find(p=>p.name===pin).roles,['open']);
      assert.equal(result.models.open.candidates[0].pins.find(p=>p.name===pin).binding.open,true);
      assert.doesNotMatch(result.generated.code,new RegExp(`\\b${pin}\\b`),'Open pins must be omitted, without invented constants or outputs');
      assert.equal(result.generated.diff.find(p=>p.pin===pin).rule,'open');
      assert.ok(result.generated.warnings.some(w=>w.includes(pin)),'Draft explains each open pin');
    }
    assert.match(result.generated.code,/Sense := "NewSense"/);
    assert.match(result.generated.code,/Valve => "NewValve"/);
    assert.ok(!result.roles.find(p=>p.name==='Sense').roles.includes('open'));
    assert.ok(result.cannotDropConnected);
    assert.ok(result.cannotGenerateUnsupported);
    assert.ok(result.malformed);
    assert.ok(result.feedbackRoles.includes('feedback'));
    assert.ok(!result.feedbackRoles.includes('shared'));
    assert.match(result.feedback.code,/Sense := "NewValve"/);
    assert.match(result.feedback.code,/Valve => "NewValve"/);
    assert.doesNotMatch(result.feedback.code,/OldValve/);
    assert.ok(result.cannotKeepDonorFeedback);
    assert.ok(result.feedbackMissingOutput);
    assert.ok(result.feedbackWrongOutput);
    for (const name of ['missing','inout','logic','conditional','duplicate','branched']) {
      assert.equal(result.models[name].samples.length,0,`${name} must remain unsupported`);
      assert.equal(result.models[name].candidates.length,1,`${name} must remain visible`);
      assert.equal(result.models[name].candidates[0].supported,false);
      assert.ok(result.models[name].candidates[0].reason,`${name} must explain the limitation`);
    }
    assert.match(result.models.missing.candidates[0].reason,/Home/);
    assert.match(result.models.inout.candidates[0].reason,/State/);
    assert.equal(result.models.multi.samples.length,0);
    assert.deepEqual(result.models.multi.candidates.map(s=>s.id),['Z01','Z02']);
    assert.ok(result.models.multi.candidates.every(s=>!s.supported && s.reason));
    assert.equal(result.models.mixed.candidates.length,2);
    assert.deepEqual(result.models.mixed.samples,['Z01']);
    assert.deepEqual(result.models.mixed.instances,['z01','z02']);
    assert.ok(result.models.logic.occupied.includes('"oldvalve"'),'Unsupported calls still reserve their wired outputs');
    assert.ok(result.models.logic.endpointSymbols.some(e=>e.symbol==='"OldSense"'),'Unsupported networks still establish cylinder endpoint addresses');
    assert.ok(result.models.open.hostSymbols.includes('oldsense'),'All existing host operands remain reserved');
    assert.equal(result.models.endpointLogic.candidates[1].supported,false);
    assert.deepEqual(result.endpoints.map(p=>p.id),['after','before'],'Last cylinder includes a sensor behind a contact in an unsupported network');
    assert.equal(result.endpoints[0].after_address,'I52.7');
    assert.deepEqual(result.models.eno.samples,['Z01'],'Open ENO does not turn an unconditional call into a conditional call');
    console.log('PASS: browser XML extraction preserves open pins, exposes all candidates, rejects unsupported wiring and retains collision coverage.');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
