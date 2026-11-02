from Op275RetestBridge import Bridge,ROOT
import json,re
from xml.dom import minidom as D
out=ROOT/'artifacts/op275-retest-20261029';gen=out/'candidate';project=json.loads((out/'project.json').read_text(encoding='utf-8'))['project_path'];blocks=json.loads((out/'blocks.json').read_text(encoding='utf-8'));b=Bridge(out/'apply-final')
try:
 baseline=json.loads((out/'graph-model.json').read_text(encoding='utf-8'))
 r=b.call('tia_generate_graph',project_path=project,model_json=(gen/'graph-model.json').read_text(encoding='utf-8'),base_xml=baseline['base_xml'],include_model=False)
 (gen/'OP275_自动流程.xml').write_text(r['xml'],encoding='utf-8')
 import subprocess,sys
 subprocess.run([sys.executable,str(ROOT/'tests/Test-Op275Retest.py')],check=True)
 b.call('tia_connect',project_path=project)
 entry=next(x for x in blocks if x['name']=='DB_OP275Product')
 b.call('tia_compile_plc',project_path=project)
 entry=next(x for x in blocks if x['number']==30161 and x['language']=='GRAPH');name=entry['name']
 graph=D.parse(str(gen/(name+'.xml'))).getElementsByTagNameNS('*','Graph')[0].toxml();graph=re.sub(r'="[^"]*"',lambda m:m.group().replace('\n','&#xA;').replace('\r','&#xD;').replace('\t','&#x9;'),graph);patches=json.dumps([dict(op='replace_xml',xpath='//g:Graph',xml=graph)],ensure_ascii=False)
 r=b.call('tia_update_graph',project_path=project,block_name=name,group_path=entry['group_path'],patches_json=patches,expected_hash=baseline['graph_hash'],dry_run=False,confirm=True,compile_mode='plc')
 (out/'compile-after.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(r),flush=True)
 for name in ['DB_OP275Product','OP275_ProductInfo','OP275_BranchControl','OP275_Alarm',entry['name']]:
  e=next(x for x in blocks if x['name']==name)
  r=b.call('tia_read_block',project_path=project,block_name=name,group_path=e['group_path'])
  (out/'apply'/(name+'.after.xml')).write_text(r['xml'],encoding='utf-8')
finally:b.close()
