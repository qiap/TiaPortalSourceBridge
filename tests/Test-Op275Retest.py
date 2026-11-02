import json,re,copy
from pathlib import Path
from xml.etree import ElementTree as E
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/op275-retest-20261029';GEN=OUT/'candidate'
checks=[]
def check(name,ok):
 assert ok,name
 checks.append(name)
def local(n):return n.tag.split('}')[-1]
def canonical(n):return (n.tag,tuple(sorted(n.attrib.items())),(n.text or '').strip(),tuple(canonical(c) for c in n))
graph=E.parse(GEN/'OP275_自动流程.xml');old=E.parse(OUT/'capture/OP275_自动流程.xml')
trans={int(x.get('Number')):x for x in graph.findall('.//{*}Transition')};steps={int(x.get('Number')):x for x in graph.findall('.//{*}Step')}
for kind in ('Step','Transition'):
 for before in old.findall('.//{*}'+kind):
  after=(steps if kind=='Step' else trans)[int(before.get('Number'))]
  check('original '+kind+' '+before.get('Number')+' unchanged',canonical(before)==canonical(after))
def eval_transition(number,values):
 net=trans[number].find('{*}FlgNet');parts={x.get('UId'):x for x in net.find('{*}Parts')};inputs={}
 for wire in net.find('{*}Wires'):
  ends=list(wire);source=next(x for x in ends if local(x) in ('IdentCon','Powerrail') or x.get('Name')=='out')
  for x in ends:
   if x is not source:inputs[(x.get('UId'),x.get('Name'))]=source
 def get(uid,pin,default=False):
  s=inputs.get((uid,pin))
  if s is None:return default
  if local(s)=='Powerrail':return True
  p=parts[s.get('UId')]
  if local(s)=='IdentCon':
   if p.get('Scope')=='LiteralConstant':
    text=p.findtext('.//{*}ConstantValue');return int(text[2:-1])*1000 if text.startswith('T#') else int(text)
   key=('.'.join(x.get('Name') for x in p.findall('.//{*}Component')))
   if p.get('Scope')=='LocalVariable':key='#'+key
   return values.get(key,False)
  return output(s.get('UId'))
 def output(uid):
  part=parts[uid];name=part.get('Name')
  if name=='Contact':return get(uid,'in') and (not get(uid,'operand') if part.find('{*}Negated') is not None else bool(get(uid,'operand')))
  if name in ('Ge','Le','Eq','Ne','Gt','Lt'):
   a,b=get(uid,'in1'),get(uid,'in2');return get(uid,'pre',True) and {'Ge':a>=b,'Le':a<=b,'Eq':a==b,'Ne':a!=b,'Gt':a>b,'Lt':a<b}[name]
  if name=='O':return any(get(uid,'in'+str(i)) for i in range(1,int(part.find('{*}TemplateValue').text)+1))
  if name=='TrCoil':return get(uid,'in')
  raise AssertionError(name)
 return output(next(uid for uid,x in parts.items() if x.get('Name')=='TrCoil'))
P='DB_OP275Product.';B='DB_OP275BGraph.';R='DB_OP275Branch.';C='DB_OP275Cylinder.';S='DB_OP275BasicSign.'
v={P+'ActiveValid':True,P+'LeakRetestRequested':True,P+'LeakRetestUsed':False,S+'Permit.Enabled':True,R+'IO.SafetyRelayReady':True,R+'GraphAdvancePermit':True,S+'Permit.RobotClear':True,R+'IO.PartPresent':True,R+'IO.LevelLeft':True,C+'Z04.Wpos_SE':True}
check('first NG routes to retry',eval_transition(224,v));check('first NG cannot route to final NG',not eval_transition(222,v))
v[P+'LeakRetestUsed']=True;check('retry cannot be consumed twice',not eval_transition(224,v))
clock='#'+steps[70].get('Name')+'.T';v[clock]=1999;v['DB_OP275Config.ExhaustDelay']=2000;v[P+'RetestStopMinimum']=2000
check('no retract before minimum wait',not eval_transition(270,v));v[clock]=2000;check('stop and elapsed wait permit retract',eval_transition(270,v))
v['DB_OP275Config.ExhaustDelay']=5000;check('configured longer wait honored',not eval_transition(270,v));v[clock]=5000;check('longer wait completion',eval_transition(270,v))
for key,val in [(R+'IO.LeakRunning',True),(C+'Z04.Wpos_SE',False),(S+'Permit.RobotClear',False),(R+'IO.PartPresent',False),(R+'IO.LevelLeft',False),(R+'GraphAdvancePermit',False),(S+'Command.RunInhibit',True)]:
 t=dict(v);t[key]=val;check('retract blocked '+key,not eval_transition(270,t))
v[C+'Z05.Wpos_SE']=True
check('side return needs actual home',not eval_transition(271,v));v[C+'Z07.Hpos_SE']=True;check('side home allows top return',eval_transition(271,v))
check('top return needs actual home',not eval_transition(272,v));v[C+'Z05.Hpos_SE']=True;check('both home allows preparation',eval_transition(272,v))
check('old request must clear',not eval_transition(273,v));v[P+'LeakRetestRequested']=False;check('clear result allows reseal',eval_transition(273,v))
v[P+'Result.Valid']=True;v[P+'Result.NG']=True
check('second NG routes to final stop',eval_transition(222,v));check('second NG cannot retry',not eval_transition(224,v))
v[P+'Result.NG']=False;v[P+'Result.OK']=True;check('retest OK normal path',eval_transition(221,v))
for i in range(70,74):
 actions=' '.join(x.get('Text','') for x in steps[i].findall('.//{*}Actions//{*}Token'))
 check('stop held through step '+str(i),'PLCToComso.Stop' in actions)
 check('clamp never released step '+str(i),'Z04.Trigger_H' not in actions and 'UnloadRequest' not in actions)
 check('motion timeout step '+str(i),steps[i].get('MaximumStepTime')!='T#0S')
# Execute the actual added SCL state assignments with deterministic input scenarios.
code=(GEN/'added-logic.scl').read_text(encoding='utf-8')
var=r'(?:"[^"\n]+"|#\w+)(?:\.\w+)*'
def key(s):return s.replace('"','')
def expr(s,v):
 s=re.sub(var,lambda m:'V['+repr(key(m.group()))+']',s)
 for a,b in [('TRUE','True'),('FALSE','False'),('AND','and'),('OR','or'),('NOT','not')]:s=re.sub(r'\b'+a+r'\b',b,s)
 s=s.replace('<>','!=');s=re.sub(r'(?<![<>!:])=(?!=)','==',s)
 class Values(dict):
  def __missing__(self,k):return False
 return eval(' '.join(s.split()),{'__builtins__':{}},{'V':Values(v)})
def execute(code,v):
 while code.strip():
  code=code.lstrip()
  if code.startswith('IF '):
   m=re.match(r'IF (.*?) THEN\s*(.*?)END_IF;',code,re.S);assert m,code
   if expr(m[1],v):execute(m[2],v)
  else:
   m=re.match('('+var+r')\s*:=\s*(.*?);',code,re.S);assert m,code
   v[key(m[1])]=expr(m[2],v)
  code=code[m.end():]
q={P+'ActiveValid':True,B+'LatchResult':True,'OP275_Comso_DB.FreshEnd':True,R+'IO.LeakNG':True,B+'OP275自动流程工步':18}
execute(code,q);check('first valid NG requests retry',q[P+'LeakRetestRequested'] and q[P+'FirstLeakNG']);check('first NG no final alarm',not q[P+'LeakRetestFailed'])
q[B+'OP275自动流程工步']=70;execute(code,q);check('enter retry consumes allowance',q[P+'LeakRetestUsed'])
q.update({B+'OP275自动流程工步':73,B+'LatchResult':False,C+'Z04.Wpos_SE':True,C+'Z05.Hpos_SE':True,C+'Z07.Hpos_SE':True})
execute(code,q);check('prepare clears old result and request',not q[P+'Result.Valid'] and not q[P+'LeakRetestRequested']);check('prepare retains task and attempt',q[P+'ActiveValid'] and q[P+'LeakRetestUsed'] and q[P+'FirstLeakNG'])
q.update({B+'OP275自动流程工步':18,B+'LatchResult':True});execute(code,q);check('second NG cannot request another retry',not q[P+'LeakRetestRequested'])
q.update({B+'OP275自动流程工步':51,P+'Result.Valid':True,P+'Result.NG':True});execute(code,q);check('final NG alarm asserted',q[P+'LeakRetestFailed'])
q[B+'LatchResult']=False;q[P+'ActiveValid']=False;q[P+'Result.Valid']=False;execute(code,q);check('confirmed empty/new cycle resets retry',not q[P+'LeakRetestUsed'] and not q[P+'FirstLeakNG'])
for bad in [('OP275_Comso_DB.InvalidResult',True),(R+'IO.LeakOK',True),('OP275_Comso_DB.FreshEnd',False)]:
 q={P+'ActiveValid':True,B+'LatchResult':True,'OP275_Comso_DB.FreshEnd':True,R+'IO.LeakNG':True};q[bad[0]]=bad[1];execute(code,q);check('invalid/stale signals cannot retry '+bad[0],not q.get(P+'LeakRetestRequested',False))
# Ensure the added state machine is actually inserted before the original result latch.
p=E.parse(GEN/'OP275_ProductInfo.xml');n=p.findall('.//{*}StructuredText')[0];parts=list(n)
req=[i for i,x in enumerate(parts) if local(x)=='Access' and '.'.join(c.get('Name') for c in x.findall('.//{*}Component'))==P+'LeakRetestRequested']
latch=[i for i,x in enumerate(parts) if local(x)=='Access' and '.'.join(c.get('Name') for c in x.findall('.//{*}Component'))==B+'LatchResult']
check('request processing before original final latch',len(latch)==2 and any(latch[0]<i<latch[1] for i in req))
check('original final latch gated by pending retry',any(x.get('Text')=='NOT' for x in parts[latch[1]:latch[1]+70]) and any(latch[1]<i<latch[1]+70 for i in req))
(OUT/'logic-test-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'scope':'Offline evaluation of generated GRAPH conditions and added SCL; not a PLC/physical simulation.'},ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS',len(checks),'offline logic/preservation checks')

