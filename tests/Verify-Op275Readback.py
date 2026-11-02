"""Verify exported TIA blocks without reconnecting or changing the project."""
import json
from pathlib import Path
from xml.etree import ElementTree as E

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/op275-retest-20261029'

def local(e):
    return e.tag.split('}')[-1]

def normalize(e):
    # Siemens combines whitespace tokens and adds empty action editor rows.
    if local(e) in ('Blank', 'NewLine'):
        return None
    if local(e) == 'Action' and not len(e) and not e.attrib:
        return None
    if local(e) == 'BooleanAttribute' and e.get('Name') == 'SetPoint' and e.text == 'false':
        return None
    attrs = tuple(sorted((k, v) for k, v in e.attrib.items()
                         if k not in ('UId', 'ID') and not (k == 'IsMissing' and v == 'false')))
    children = [n for c in e if (n := normalize(c)) is not None]
    if local(e) == 'Member':
        children.sort(key=repr)  # StartValue position is canonicalized on import.
    return e.tag, attrs, (e.text or '').strip(), tuple(children)

checks = []
for name in ('DB_OP275Product', 'OP275_ProductInfo', 'OP275_BranchControl', 'OP275_Alarm'):
    before = E.parse(OUT / 'candidate' / (name + '.xml'))
    after = E.parse(OUT / 'apply' / (name + '.after.xml'))
    tags = ('Sections',) if name == 'DB_OP275Product' else ('StructuredText', 'FlgNet')
    for tag in tags:
        a, b = before.findall('.//{*}' + tag), after.findall('.//{*}' + tag)
        assert len(a) == len(b), (name, tag, 'network count')
        for i, (x, y) in enumerate(zip(a, b)):
            assert normalize(x) == normalize(y), (name, tag, i)
            checks.append(f'{name} {tag} {i}: readback matches candidate')

# Re-run transition scenarios on the real export, including UID wire references.
# The existing original-network checks are deliberately left unmodified.
test = ROOT / 'tests/Test-Op275Retest.py'
source = test.read_text(encoding='utf-8-sig')
source = source.replace("E.parse(GEN/'OP275_自动流程.xml')", "E.parse(OUT/'apply/OP275_自动流程.after.xml')")
source = source.replace("E.parse(GEN/'OP275_ProductInfo.xml')", "E.parse(OUT/'apply/OP275_ProductInfo.after.xml')")
source = source.replace('logic-test-results.json', 'readback-logic-test-results.json')
source = source.replace('Offline evaluation of generated GRAPH conditions and added SCL;',
                        'Offline evaluation of TIA readback GRAPH and verified equivalent SCL;')
exec(compile(source, str(test), 'exec'), {'__file__': str(test)})
(OUT / 'readback-equivalence-results.json').write_text(
    json.dumps({'passed': len(checks), 'checks': checks}, indent=2, ensure_ascii=False), encoding='utf-8')
print('PASS', len(checks), 'supporting-block readback equivalence checks')
