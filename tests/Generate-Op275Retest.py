from Op275RetestBridge import Bridge,ROOT
import json
out=ROOT/'artifacts/op275-retest-20261029';gen=out/'candidate';project=json.loads((out/'project.json').read_text(encoding='utf-8'))['project_path'];b=Bridge(out/'generate')
try:
 r=b.call('tia_generate_graph',project_path=project,model_json=(gen/'graph-model.json').read_text(encoding='utf-8'),base_xml=json.loads((out/'graph-model.json').read_text(encoding='utf-8'))['base_xml'],include_model=False)
 (gen/'OP275_自动流程.xml').write_text(r['xml'],encoding='utf-8')
 b.call('tia_connect',project_path=project)
 r=b.call('tia_compile_plc',project_path=project)
 (out/'compile-before.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps(r,ensure_ascii=False),flush=True)
finally:b.close()
