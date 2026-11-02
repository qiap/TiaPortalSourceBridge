"""Offline stdlib regression: no TIA connection, import, compile, save or download."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import statistics
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def rpc_batch(exe, calls, config=None):
    requests = [dict(jsonrpc='2.0', id=i + 1, method=method, params=params)
                for i, (method, params) in enumerate(calls)]
    started = time.perf_counter()
    result = subprocess.run([str(exe), 'mcp', '--config', str(config or ROOT / 'mcp-config.json')],
                            input=''.join(json.dumps(r, ensure_ascii=True) + '\n' for r in requests),
                            text=True, encoding='utf-8-sig', capture_output=True, timeout=30)
    elapsed = (time.perf_counter() - started) * 1000
    assert result.returncode == 0, result.stderr
    responses = [json.loads(s) for s in result.stdout.splitlines() if s.strip()]
    assert [r['id'] for r in responses] == list(range(1, len(requests) + 1))
    assert all('error' not in r for r in responses), responses
    return [r['result'] for r in responses], elapsed


def tool(name, **arguments):
    return ('tools/call', dict(name=name, arguments=arguments))


def main(exe, source):
    checks = []

    def check(name, condition):
        assert condition, name
        checks.append(name)

    catalog = json.loads((exe.parent / 'templates/process/catalog.json').read_text(encoding='utf-8'))
    entries = catalog['entries']
    init = ('initialize', dict(protocolVersion='2025-06-18', capabilities={}, clientInfo=dict(name='offline-library-test', version='1')))
    initial, _ = rpc_batch(exe, [init, ('tools/list', {}), tool('tia_list_process_library')])
    check('version', initial[0]['serverInfo']['version'] == '3.2.0')
    new_tools = {t['name']: t for t in initial[1]['tools']}
    check('69 tools', len(new_tools) == 69)
    baseline, _ = rpc_batch(ROOT / 'src/TiaPortalSourceBridge/bin/Release/net48/TiaPortalSourceBridge.exe', [('tools/list', {})])
    def without_descriptions(value):
        if isinstance(value, dict):
            return {k: without_descriptions(v) for k, v in value.items() if k != 'description'}
        if isinstance(value, list):
            return [without_descriptions(v) for v in value]
        return value
    check('existing tool names and structural schemas preserved', all(without_descriptions(new_tools[t['name']]) == without_descriptions(t) for t in baseline[0]['tools']))
    check('catalog complete', initial[2]['structuredContent']['total'] == len(entries) == 16)
    check('catalog has no template bodies', all('body' not in e for e in initial[2]['structuredContent']['entries']))
    check('source snapshot date retained', catalog['source']['exported_at_utc'].startswith('2026-10-14'))

    calls, cases = [], []

    def case(name, request, predicate):
        calls.append(request)
        cases.append((name, predicate))

    case('Chinese query', tool('tia_list_process_library', query='气缸'), lambda r: r['structuredContent']['total'] >= 3)
    case('template filter', tool('tia_list_process_library', kind='template'), lambda r: r['structuredContent']['total'] == 7)
    case('knowledge filter', tool('tia_list_process_library', kind='knowledge'), lambda r: r['structuredContent']['total'] == 9)
    case('paging', tool('tia_list_process_library', offset=0, limit=2), lambda r: len(r['structuredContent']['entries']) == 2 and r['structuredContent']['next_offset'] == 2)
    case('no matches', tool('tia_list_process_library', query='no_such_equipment_123'), lambda r: r['structuredContent']['total'] == 0)
    for name, request in [
        ('bad kind', tool('tia_list_process_library', kind='bad')),
        ('unknown id', tool('tia_read_process_library', id='missing')),
        ('path traversal id', tool('tia_read_process_library', id='../mcp-config.json')),
        ('knowledge cannot render', tool('tia_render_process_template', id='op270.alarm-semantics', parameters_json='{}')),
        ('missing parameters', tool('tia_render_process_template', id='op270.cylinder-db', parameters_json='{}')),
        ('bad JSON', tool('tia_render_process_template', id='op270.cylinder-db', parameters_json='{')),
        ('non-object JSON', tool('tia_render_process_template', id='op270.cylinder-db', parameters_json='[]')),
    ]:
        case(name, request, lambda r: r.get('isError') is True and 'structuredContent' not in r)

    sample = dict(block_name='DB_ExampleCylinder', cylinder_id='Z14', timeout_set=15,
                  instance='#Z14', cylinder_db='DB_ExampleCylinder', alarm_db='DB_ExampleAlarm', alarm_index=14,
                  basic_db='DB_ExampleBasic', first_cycle='"FirstScan"', reset='"DB_ExampleBasic".Mode.FaultReset')
    generated_cases = []
    for entry in entries:
        detail = json.loads((exe.parent / 'templates/process' / entry['path']).read_text(encoding='utf-8'))
        case('read ' + entry['id'], tool('tia_read_process_library', id=entry['id']),
             lambda r, id=entry['id']: not r.get('isError') and r['structuredContent']['id'] == id and bool(r['structuredContent']['sources']))
        if entry['kind'] != 'template':
            continue
        values = {p['name']: sample.get(p['name'], '"DB_ExampleSignals".' + p['name'])
                  for p in detail['parameters'] if 'default' not in p}
        generated_cases.append((entry['id'], values))
        case('render ' + entry['id'], tool('tia_render_process_template', id=entry['id'], parameters_json=json.dumps(values)),
             lambda r: not r.get('isError') and '{{' not in r['structuredContent']['content']
             and all(r['structuredContent'][k] is False for k in ['imported', 'compiled', 'saved', 'downloaded']))

    valid_db = dict(block_name='DB_ExampleCylinder', cylinder_id='Z14', timeout_set=15)
    for name, values in [
        ('unknown parameter', dict(valid_db, mystery=1)),
        ('identifier injection', dict(valid_db, block_name='X"; END_DATA_BLOCK')),
        ('negative time', dict(valid_db, timeout_set=-1)),
        ('too large time', dict(valid_db, timeout_set=32001)),
        ('nonintegral time', dict(valid_db, timeout_set=1.5)),
        ('object parameter', dict(valid_db, cylinder_id={'x': 1})),
        ('boolean parameter', dict(valid_db, timeout_set=True)),
    ]:
        case(name, tool('tia_render_process_template', id='op270.cylinder-db', parameters_json=json.dumps(values)), lambda r: r.get('isError') is True)
    for name, values in [
        ('duplicate GRAPH members', dict(block_name='DB_Test', auto_member='Same', reset_member='same')),
        ('reserved GRAPH member', dict(block_name='DB_Test', auto_member='InitialDone')),
    ]:
        case(name, tool('tia_render_process_template', id='op270.graph-db', parameters_json=json.dumps(values)), lambda r: r.get('isError') is True)
    cylinder_values = next(v for id, v in generated_cases if id == 'op270.cylinder-call')
    for name, value in [('symbol injection', 'X; Y := TRUE'), ('symbol newline', '"X\nY"'), ('index range', 33)]:
        changed = dict(cylinder_values)
        changed['alarm_index' if name == 'index range' else 'work_sensor'] = value
        case(name, tool('tia_render_process_template', id='op270.cylinder-call', parameters_json=json.dumps(changed)), lambda r: r.get('isError') is True)
    case('Unicode identifiers', tool('tia_render_process_template', id='op270.cylinder-db', parameters_json=json.dumps(dict(valid_db, block_name='DB_示例气缸'))), lambda r: not r.get('isError') and 'DB_示例气缸' in r['structuredContent']['content'])
    case('LAD generation regression', tool('tia_generate_lad', block_name='Offline_Regression', block_type='FC', block_number=32000,
         networks_json=json.dumps([dict(coil='Motor', branches=[[dict(symbol='Start')]])])),
         lambda r: not r.get('isError') and ET.fromstring(r['structuredContent']['xml']).tag == 'Document')
    case('no project workers', tool('tia_list_connections'), lambda r: not r.get('isError')
         and r['structuredContent']['managed_project_count'] == 0 and r['structuredContent']['projects'] == [])
    responses, elapsed = rpc_batch(exe, calls)
    for (name, predicate), response in zip(cases, responses):
        check(name, predicate(response))

    # Runtime assets must follow the executable, even with a project config elsewhere.
    with tempfile.TemporaryDirectory() as tmp:
        cfg = Path(tmp) / 'external-config.json'
        cfg.write_text('{}', encoding='utf-8')
        relocated, _ = rpc_batch(exe, [tool('tia_read_process_library', id='op270.cylinder-conventions')], cfg)
        check('external config does not change library root', not relocated[0].get('isError'))
        spec = importlib.util.spec_from_file_location('library_builder', ROOT / 'scripts/Build-Op270ProcessLibrary.py')
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        out = Path(tmp) / 'regenerated'
        mod.build(source, out)
        check('reproducible library extraction', all((out / p.relative_to(exe.parent / 'templates/process')).read_bytes() == p.read_bytes()
              for p in (exe.parent / 'templates/process').rglob('*.json')))
    detail = json.loads((exe.parent / 'templates/process/op270/op270.source-inventory.json').read_text(encoding='utf-8'))
    check('266 source hashes verified', len(detail['files']) == 266 and all(hashlib.sha256((source / r['path']).read_bytes()).hexdigest() == r['sha256'] for r in detail['files']))
    # Save concrete reviewable render examples. None are sent to TIA.
    examples = ROOT / 'examples/process-library'
    examples.mkdir(parents=True, exist_ok=True)
    rendered, _ = rpc_batch(exe, [tool('tia_render_process_template', id=id, parameters_json=json.dumps(values)) for id, values in generated_cases])
    for (id, values), result in zip(generated_cases, rendered):
        data = result['structuredContent']
        suffix = '.udt' if data['format'] == 'UDT' else '.db' if 'DATA_BLOCK' in data['content'] else '.scl'
        (examples / (id + suffix)).write_text(data['content'], encoding='utf-8')
        (examples / (id + '.parameters.json')).write_text(json.dumps(values, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    cold_times = []
    for _ in range(3):
        _, ms = rpc_batch(exe, [tool('tia_list_process_library', query='气缸')])
        cold_times.append(ms)
    baseline_exe = ROOT / 'src/TiaPortalSourceBridge/bin/Release/net48/TiaPortalSourceBridge.exe'
    report = dict(passed=len(checks), failed=0, checks=checks, test_scope='offline; no TIA connection or PLC/HMI mutation',
                  templates=7, knowledge=9, library_bytes=sum(f.stat().st_size for f in (exe.parent / 'templates/process').rglob('*') if f.is_file()),
                  baseline_exe_bytes=baseline_exe.stat().st_size, candidate_exe_bytes=exe.stat().st_size,
                  candidate_sha256=hashlib.sha256(exe.read_bytes()).hexdigest(), batch_requests=len(calls), batch_ms=round(elapsed, 2),
                  cold_query_process_median_ms=round(statistics.median(cold_times), 2))
    (ROOT / 'tests/process-library-results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'checks'}, ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, default=ROOT / 'artifacts/process-library-3.2/TiaPortalSourceBridge.exe')
    parser.add_argument('--source', type=Path, required=True)
    args = parser.parse_args()
    main(args.exe.resolve(), args.source.resolve())
