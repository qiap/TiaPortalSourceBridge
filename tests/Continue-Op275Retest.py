from Op275RetestBridge import Bridge,ROOT
import json
from xml.dom import minidom as D
out=ROOT/'artifacts/op275-retest-20261029';gen=out/'candidate';project=json.loads((out/'project.json').read_text(encoding='utf-8'))['project_path'];blocks=json.loads((out/'blocks.json').read_text(encoding='utf-8'));b=Bridge(out/'apply-continue')
try:
 b.call('tia_connect',project_path=project)
 b.call('tia_compile_plc',project_path=project)
 for name in ['OP275_ProductInfo','OP275_BranchControl','OP275_Alarm']:
  entry=next(x for x in blocks if x['name']==name)
  b.call('tia_import_block',project_path=project,group_path=entry['group_path'],xml=(gen/(name+'.xml')).read_text(encoding='utf-8'),confirm=True,compile_mode='none')
 entry=next(x for x in blocks if x['number']==30161 and x['language']=='GRAPH');name=entry['name']
 graph=D.parse(str(gen/(name+'.xml'))).getElementsByTagNameNS('*','Graph')[0].toxml();patches=json.dumps([dict(op='replace_xml',xpath='//g:Graph',xml=graph)],ensure_ascii=False)
 r=b.call('tia_update_graph',project_path=project,block_name=name,group_path=entry['group_path'],patches_json=patches,expected_hash=json.loads((out/'graph-model.json').read_text(encoding='utf-8'))['graph_hash'],dry_run=False,confirm=True,compile_mode='plc')
 (out/'compile-after.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(r,ensure_ascii=False),flush=True)
 for name in ['DB_OP275Product','OP275_ProductInfo','OP275_BranchControl','OP275_Alarm',entry['name']]:
  entry2=next(x for x in blocks if x['name']==name)
  r=b.call('tia_read_block',project_path=project,block_name=name,group_path=entry2['group_path'])
  (out/'apply'/(name+'.after.xml')).write_text(r['xml'],encoding='utf-8')
finally:b.close()
