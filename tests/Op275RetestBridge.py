import json, subprocess, time
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
ROOT=Path(__file__).resolve().parents[1]
class Bridge:
 def __init__(self,out):
  self.out=Path(out);self.out.mkdir(parents=True,exist_ok=True);self.seq=0;self.records=[]
  self.log=(self.out/'bridge-stderr.log').open('a',encoding='utf-8')
  self.p=subprocess.Popen([str(ROOT/'src/TiaPortalSourceBridge/bin/Release/net48/TiaPortalSourceBridge.exe'),'mcp','--config',str(ROOT/'mcp-config.json')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,text=True,encoding='utf-8')
  self.pool=ThreadPoolExecutor(max_workers=1)
 def call(self,name,**args):
  self.seq+=1;start=time.perf_counter();args.setdefault('response_mode','compact')
  self.p.stdin.write(json.dumps(dict(jsonrpc='2.0',id=self.seq,method='tools/call',params=dict(name=name,arguments=args)),ensure_ascii=False)+'\n');self.p.stdin.flush()
  line=self.pool.submit(self.p.stdout.readline).result(timeout=600)
  reply=json.loads(line);r=reply.get('result',reply)
  self.records.append(dict(tool=name,elapsed_ms=(time.perf_counter()-start)*1000,result=r))
  (self.out/'rpc-results.json').write_text(json.dumps(self.records,ensure_ascii=False,indent=2),encoding='utf-8')
  print(name,round(self.records[-1]['elapsed_ms']), 'ms', 'ERROR' if r.get('isError') or 'error' in reply else 'OK',flush=True)
  if r.get('isError') or 'error' in reply:raise RuntimeError(json.dumps(r,ensure_ascii=False))
  return r.get('structuredContent',{})
 def close(self):
  self.p.stdin.close()
  try:self.p.wait(timeout=10)
  except subprocess.TimeoutExpired:pass
  self.pool.shutdown(wait=False);self.log.close()
if __name__=='__main__':
 out=ROOT/'artifacts/op275-retest-20261029';b=Bridge(out/'capture')
 try:
  processes=b.call('tia_list_open_projects',portal_version='V17')['processes']
  paths=list({x['project_path'] for x in processes if x.get('project_path')})
  assert len(paths)==1,paths
  project=paths[0];(out/'project.json').write_text(json.dumps({'project_path':project},ensure_ascii=False),encoding='utf-8')
  b.call('tia_connect',project_path=project)
  blocks=b.call('tia_list_blocks',project_path=project,refresh=True)['blocks']
  (out/'blocks.json').write_text(json.dumps(blocks,ensure_ascii=False,indent=2),encoding='utf-8')
  for x in blocks:
   if x['name'].startswith(('OP275_','DB_OP275')):
    r=b.call('tia_read_block',project_path=project,block_name=x['name'],group_path=x['group_path'] or '/')
    (out/'capture'/(x['name']+'.xml')).write_text(r['xml'],encoding='utf-8')
  g=next(x for x in blocks if x['number']==30161 and x['language']=='GRAPH')
  model=b.call('tia_read_graph_model',project_path=project,block_name=g['name'],group_path=g['group_path'])
  (out/'graph-model.json').write_text(json.dumps(model,ensure_ascii=False,indent=2),encoding='utf-8')
 finally:b.close()
