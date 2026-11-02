import json,subprocess,time,statistics,sys
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, TimeoutError
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/leak-retest-review'
OUT.mkdir(exist_ok=True)
project=json.loads((ROOT/'artifacts/live-discovery.json').read_text(encoding='utf-8'))['result']['structuredContent']['processes'][0]['project_path']
log=(OUT/'stderr.log').open('w',encoding='utf-8')
p=subprocess.Popen([str(ROOT/'src/TiaPortalSourceBridge/bin/Release/net48/TiaPortalSourceBridge.exe'),'mcp','--config',str(ROOT/'mcp-config.json')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8')
pool=ThreadPoolExecutor(max_workers=1); seq=0; records=[]
def call(name,arguments=None):
 global seq
 seq+=1
 args=dict(project_path=project,response_mode='compact');args.update(arguments or {})
 request=dict(jsonrpc='2.0',id=seq,method='tools/call',params=dict(name=name,arguments=args))
 start=time.perf_counter();p.stdin.write(json.dumps(request,ensure_ascii=False)+'\n');p.stdin.flush()
 future=pool.submit(p.stdout.readline)
 try: line=future.result(timeout=600)
 except TimeoutError:
  print('TIMEOUT '+name,flush=True);raise
 elapsed=(time.perf_counter()-start)*1000
 r=json.loads(line); result=r.get('result',r)
 record=dict(tool=name,arguments=args,elapsed_ms=round(elapsed,3),bytes=len(line.encode('utf-8')),isError=result.get('isError',False),performance=result.get('_meta',{}).get('performance',{}))
 records.append(record)
 (OUT/'calls.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
 if result.get('isError') or 'error' in r: raise RuntimeError(json.dumps(result,ensure_ascii=False))
 print(name,round(elapsed,2),'ms',flush=True)
 return result.get('structuredContent',{})
def measure(label,name,args):
 call(name,args)
 start=len(records)
 for _ in range(5):call(name,args)
 values=records[start:]
 return dict(label=label,tool=name,median_ms=round(statistics.median(v['elapsed_ms'] for v in values),3),min_ms=min(v['elapsed_ms'] for v in values),max_ms=max(v['elapsed_ms'] for v in values),median_bytes=statistics.median(v['bytes'] for v in values))
try:
 call('tia_connect')
 blocks=call('tia_list_blocks',{'refresh':True})['blocks']
 (OUT/'blocks.json').write_text(json.dumps(blocks,ensure_ascii=False,indent=2),encoding='utf-8')
 selected=[b for b in blocks if b['name'] in ('OP275_BranchControl','DB_OP275Branch','OP275_ProductInfo','DB_OP275Product','OP275_Comso','OP275_Comso_DB','OP275_Cylinder','DB_OP275Cylinder','OP275_Permit','OP275_Main')]
 for b in selected:
  data=call('tia_read_block',{'block_name':b['name'],'group_path':b.get('group_path') or '/'})
  (OUT/(b['name']+'.xml')).write_text(data['xml'],encoding='utf-8')
 print(json.dumps([b for b in blocks if b['name'].startswith('OP275')],ensure_ascii=False),flush=True)
finally:
 p.stdin.close()
 try:p.wait(timeout=10)
 except subprocess.TimeoutExpired:pass
 pool.shutdown(wait=False);log.close()
