"""End-to-end cache reads through both MCP engines. No TIA process or connection needed."""
import hashlib, json, pathlib, shutil, subprocess, sys, tempfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
checks=0
def check(ok,message):
    global checks
    assert ok,message
    checks+=1
def rpc(exe,cfg,calls):
    data=''.join(json.dumps(dict(jsonrpc='2.0',id=i+1,method=m,params=p))+'\n' for i,(m,p) in enumerate(calls))
    p=subprocess.run([str(exe),'mcp','--config',str(cfg)],input=data,text=True,capture_output=True,encoding='utf-8-sig',timeout=45)
    assert p.returncode==0,p.stderr
    rows=[json.loads(x) for x in p.stdout.splitlines() if x.strip()]
    assert all('result' in x for x in rows),rows
    return [x['result'] for x in rows]
with tempfile.TemporaryDirectory(prefix='tia-block-read-') as temp:
    root=pathlib.Path(temp);main=root/'TiaPortalSourceBridge.exe';shutil.copy2(sys.argv[1],main)
    engine=root/'engines/v21';v21=engine/'src/TiaPortalSourceBridge/bin/Release/net48/TiaPortalSourceBridge.exe';v21.parent.mkdir(parents=True);shutil.copy2(sys.argv[2],v21)
    configs={}
    for version,directory in [('V17',root),('V21',engine)]:
        project=directory/('Offline.ap'+version[1:]);project.write_text('',encoding='utf-8')
        cfg=directory/'mcp-config.json';cfg.write_text(json.dumps(dict(tiaPortalVersion=version,projectPath=str(project),plcName='PLC',sourceDirectory=str(directory/'source'),connectionMode='AttachOnly',allowProjectUpgrade=False)),encoding='utf-8');configs[version]=(cfg,project)
        index=directory/'.tia-source-index'/project.name;(index/'source').mkdir(parents=True);(index/'content').mkdir()
        body='<SW.Blocks.FB ID="0"><AttributeList><Name>A</Name><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource><FlgNet><Parts><Part Name="Coil" UId="2"></Part></Parts><Wires><Wire UId="3"><Powerrail></Powerrail><NameCon UId="2" Name="in"></NameCon></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB>'
        digest=hashlib.sha256(body.encode()).hexdigest();xml='<Document><Engineering version="'+version+'"></Engineering>'+body+'</Document>'
        (index/'source'/f'{digest}.xml').write_text(xml,encoding='utf-8');(index/'content'/f'{digest}.json').write_text('{}',encoding='utf-8')
        entries=[dict(id='a',kind='plc.block',owner='PLC',group='G',unit_name='',object_namespace='',name='A',language='LAD',state='ready',content_hash=digest,indexed_utc='2026-11-01T00:00:00Z'),dict(id='ghost',kind='plc.block',owner='PLC',group='G',unit_name='',name='Ghost',state='catalog_only',content_hash='')]
        (index/'catalog.json').write_text(json.dumps(dict(schema=1,project_file=project.name,portal_version=version,built_utc='2026-11-01T00:00:00Z',objects=entries,warnings=[])),encoding='utf-8')
    catalog=rpc(v21,configs['V21'][0],[('tools/list',{})])[0]
    (engine/'tool-catalog.json').write_text(json.dumps(catalog['tools']),encoding='utf-8')
    for exe,version in [(main,'V17'),(v21,'V21'),(main,'V21')]:
        cfg,project=configs[version];args=dict(project_path=str(project),plc_name='PLC',group_path='G',read_mode='cache')
        calls=[('tools/list',{}),('tools/call',dict(name='tia_read_block',arguments=dict(args,block_name='A',format='readable'))),('tools/call',dict(name='tia_read_blocks_batch',arguments=dict(args,block_names=['A','Ghost','Missing'])))]
        rows=rpc(exe,configs['V17'][0] if exe==main else cfg,calls)
        tool=next(t for t in rows[0]['tools'] if t['name']=='tia_read_blocks_batch');check(tool['annotations']['readOnlyHint'],'read-only tool catalog')
        single=rows[1];check(not single.get('isError'),str(single));data=single['structuredContent'];text=single['content'][0]['text']
        check('CompileUnit#1' in text and '普通线圈' in text,'readable text available without extra parser')
        check(data['source_kind']=='index_cache' and data['indexed_utc']=='2026-11-01T00:00:00Z' and data['write_baseline'] is False,'cache provenance through protocol')
        check(data['project_path']==str(project),'exact project routing')
        batch=rows[2];check(batch['isError'] and batch['structuredContent']['success_count']==1 and batch['structuredContent']['failure_count']==2,'partial batch not false success')
        check('catalog_only' in batch['content'][0]['text'] and 'NOT_INDEXED' in batch['content'][0]['text'],'uncached and absent explicit in text')
    cfg17,p17=configs['V17'];cfg21,p21=configs['V21']
    compare=dict(project_path_a=str(p17),plc_name_a='PLC',group_path_a='G',project_path_b=str(p21),plc_name_b='PLC',group_path_b='G',read_mode='cache')
    calls=[('tools/list',{}),('tools/call',dict(name='tia_compare_projects',arguments=compare)),
           ('tools/call',dict(name='tia_write_logic_patch',arguments=dict(project_path=str(p21),plc_name='PLC',plan_id='a'*32,confirm=True))),
           ('tools/call',dict(name='tia_write_logic_patch',arguments=dict(project_path=str(p17),plc_name='PLC',plan_id='a'*32))),
           ('tools/call',dict(name='tia_read_logic_patch_plan',arguments=dict(project_path=str(p17),plc_name='PLC',plan_id='a'*32)))]
    result=rpc(main,cfg17,calls)
    defs={t['name']:t for t in result[0]['tools']}
    check(defs['tia_compare_projects']['annotations']['readOnlyHint'],'compare read-only catalog')
    check(defs['tia_read_logic_patch_plan']['annotations']['readOnlyHint'] and not defs['tia_write_logic_patch']['annotations']['readOnlyHint'],'preview/apply annotations')
    report=result[1]['structuredContent'];byname={i['relative_identity']:i for i in report['items']}
    check(report['a']['project_path']==str(p17) and report['b']['project_path']==str(p21),'two project identities preserved')
    check(byname['A']['status']=='structurally_equal','cross-version cache compare ignores export envelope')
    check(byname['Ghost']['status']=='unknown' and result[1]['isError'],'catalog-only is not equal or removed')
    check(byname['A']['evidence_a']['write_baseline'] is False,'compare cache provenance')
    check(result[2]['isError'] and 'V16' in result[2]['content'][0]['text'] and 'V20' in result[2]['content'][0]['text'],'V21 restricted write rejected before connection')
    check(result[3]['isError'] and 'confirm' in result[3]['content'][0]['text'],'unconfirmed write rejected before connection')
    check(result[4]['isError'] and 'AttachOnly' not in result[4]['content'][0]['text'],'saved-plan lookup does not connect TIA')
    scenario=defs['tia_simulation_peripheral_start']['inputSchema']['properties']
    check('steps' in scenario['configuration_json']['description'] and 'wait_until' in scenario['configuration_json']['description'],'scenario schema exposed to agents')
    check('event_offset' in defs['tia_simulation_peripheral_status']['inputSchema']['properties'],'timeline pagination exposed')
    malformed=rpc(main,cfg17,[('tools/call',dict(name='tia_simulation_peripheral_start',arguments=dict(project_path=str(p17),instance_name='BridgeSim_Test',confirm=True,configuration_json='{"unknown_step_option":true}')))])
    check(malformed[0]['isError'] and 'unknown_step_option' in malformed[0]['content'][0]['text'],'unknown scenario fields rejected before runtime connection')
    print(f'PASS: {checks} mixed read/cache/readable, restricted edit and dual-project MCP protocol checks; no TIA connection.')
