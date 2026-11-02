"""Offline protocol tests: isolated configs, templates and V17/V21 processes."""
import json, pathlib, shutil, subprocess, sys, tempfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
checks = 0
def rpc(exe, cfg, calls):
    payload = ''.join(json.dumps(dict(jsonrpc='2.0', id=i+1, method=m, params=p))+'\n' for i,(m,p) in enumerate(calls))
    result = subprocess.run([str(exe),'mcp','--config',str(cfg)],input=payload,capture_output=True,text=True,encoding='utf-8-sig',timeout=45)
    assert result.returncode == 0, result.stderr
    rows = [json.loads(x) for x in result.stdout.splitlines() if x.strip()]
    assert [x['id'] for x in rows] == list(range(1,len(calls)+1)), rows
    assert all('error' not in x for x in rows), rows
    return [x['result'] for x in rows]
def check(ok, message):
    global checks
    assert ok, message
    checks += 1
with tempfile.TemporaryDirectory(prefix='tia-routing-') as folder:
    root = pathlib.Path(folder); engine = root/'engines/v21'; binary = engine/'src/TiaPortalSourceBridge/bin/Release/net48'; binary.mkdir(parents=True)
    main = root/'TiaPortalSourceBridge.exe'; v21 = binary/main.name
    shutil.copy2(sys.argv[1], main); shutil.copy2(sys.argv[2], v21)
    for dest, version in [(root,'V17'),(engine,'V21')]:
        cfg = dict(tiaPortalVersion=version,projectPath=str(dest/('Offline.ap'+version[1:])),plcName='PLC_1',sourceDirectory=str(dest/'source'),connectionMode='AttachOnly',allowProjectUpgrade=False,saveProjectAfterImport=False)
        (dest/'mcp-config.json').write_text(json.dumps(cfg),encoding='utf-8')
    shutil.copytree(ROOT/'templates', root/'templates')
    shutil.copytree(ROOT/'engines/v21/templates', binary/'templates')
    catalog = rpc(v21, engine/'mcp-config.json', [('tools/list',{})])[0]
    (engine/'tool-catalog.json').write_text(json.dumps(catalog['tools']),encoding='utf-8')
    base = dict(block_name='RoutingCheck',block_type='FB',block_number=32000,networks_json='[{"coil":"Motor","branches":[[{"symbol":"Start"}]]}]')
    calls = [('initialize',dict(protocolVersion='2025-06-18',capabilities={},clientInfo=dict(name='checks',version='1'))),('tools/list',{})]
    for version in ['V17','V21']:
        calls.append(('tools/call',dict(name='tia_generate_lad',arguments=dict(base,portal_version=version))))
    rows = rpc(main,root/'mcp-config.json',calls)
    check('serverInfo' in rows[0], 'initialize')
    names = [t['name'] for t in rows[1]['tools']]
    check(len(names)==len(set(names)), 'merged catalog has unique names')
    check('tia_restore_backup' in names and 'tia_read_document' in names, 'merged engine capabilities')
    for version, row in zip(['V17','V21'], rows[2:]):
        check(not row.get('isError'), str(row))
        check('version="'+version+'"' in row['structuredContent']['xml'], 'route '+version)
    print('PASS: %d dual-engine protocol and routing checks (no TIA connection).' % checks)
