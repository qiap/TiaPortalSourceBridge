from Op275RetestBridge import Bridge,ROOT
import json
from xml.etree import ElementTree as E
from xml.dom import minidom as D
out=ROOT/'artifacts/op275-retest-20261029';gen=out/'candidate';project=json.loads((out/'project.json').read_text(encoding='utf-8'))['project_path'];blocks=json.loads((out/'blocks.json').read_text(encoding='utf-8'));names=['DB_OP275Product','OP275_ProductInfo','OP275_BranchControl','OP275_Alarm'];b=Bridge(out/'apply')
def canon(n):return (n.tag,tuple(sorted((k,v) for k,v in n.attrib.items() if k not in ('UId','ID'))),(n.text or '').strip(),tuple(canon(c) for c in n))
def blockxml(text):return next(n for n in E.fromstring(text) if n.tag.startswith('SW.Blocks.'))
try:
 b.call('tia_connect',project_path=project)
 for name in names:
  entry=next(x for x in blocks if x['name']==name)
  live=b.call('tia_read_block',project_path=project,block_name=name,group_path=entry['group_path'])['xml']
  (out/'apply'/(name+'.before.xml')).write_text(live,encoding='utf-8')
  assert canon(blockxml(live))==canon(blockxml((out/'capture'/(name+'.xml')).read_text(encoding='utf-8'))),'Block changed since capture: '+name
 entry=next(x for x in blocks if x['number']==30161 and x['language']=='GRAPH');graphName=entry['name'];group=entry['group_path']
 live=b.call('tia_read_graph',project_path=project,block_name=graphName,group_path=group,include_logic=False)
 baseline=json.loads((out/'graph-model.json').read_text(encoding='utf-8'))
 assert live['graph_hash']==baseline['graph_hash'],'GRAPH changed since capture'
 document=D.parse(str(gen/(graphName+'.xml')));graph=document.getElementsByTagNameNS('*','Graph')[0].toxml()
 patches=json.dumps([dict(op='replace_xml',xpath='//g:Graph',xml=graph)],ensure_ascii=False)
 preview=b.call('tia_update_graph',project_path=project,block_name=graphName,group_path=group,patches_json=patches,expected_hash=live['graph_hash'],dry_run=True,include_xml=True)
 (out/'apply/graph-preview.json').write_text(json.dumps(preview,ensure_ascii=False,indent=2),encoding='utf-8')
 for name in names:
  entry=next(x for x in blocks if x['name']==name)
  b.call('tia_import_block',project_path=project,group_path=entry['group_path'],xml=(gen/(name+'.xml')).read_text(encoding='utf-8'),confirm=True,compile_mode='none')
 r=b.call('tia_update_graph',project_path=project,block_name=graphName,group_path=group,patches_json=patches,expected_hash=live['graph_hash'],dry_run=False,confirm=True,compile_mode='plc',include_xml=False)
 (out/'compile-after.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps(r,ensure_ascii=False),flush=True)
 for name in names+[graphName]:
  entry=next(x for x in blocks if x['name']==name)
  r=b.call('tia_read_block',project_path=project,block_name=name,group_path=entry['group_path'])
  (out/'apply'/(name+'.after.xml')).write_text(r['xml'],encoding='utf-8')
finally:b.close()
