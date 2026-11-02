from pathlib import Path
from xml.dom import minidom as D
import json,re,copy
ROOT=Path(__file__).resolve().parents[1]; OUT=ROOT/'artifacts/op275-retest-20261029'; CAP=OUT/'capture'; GEN=OUT/'candidate';GEN.mkdir(exist_ok=True)
ST='http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3'
def elems(n,name=None):return [x for x in n.childNodes if x.nodeType==x.ELEMENT_NODE and (name is None or x.localName==name)]
def desc(n,name):return list(n.getElementsByTagNameNS('*',name))
def child(n,name):return elems(n,name)[0]
def create(doc,ns,name,attrs=None,text=None):
 global uid
 attrs=dict(attrs or {})
 if ns==ST and 'UId' not in attrs:
  uid+=1;attrs['UId']=uid
 n=doc.createElementNS(ns,name)
 for k,v in (attrs or {}).items():n.setAttribute(k,str(v))
 if text is not None:n.appendChild(doc.createTextNode(text))
 return n
uid=10000
def scl(doc,code):
 global uid
 nodes=[]
 pattern=r'\n|[ \t]+|#?["\w][\w"]*(?:\.["\w][\w"]*)*|\d+|:=|<>|>=|<=|[();=<>+\-]'
 for m in re.finditer(pattern,code):
  t=m.group();uid+=1;attrs={'UId':uid}
  if t=='\n':n=create(doc,ST,'NewLine',dict(attrs,Num=1))
  elif t.isspace():n=create(doc,ST,'Blank',dict(attrs,Num=len(t)))
  elif t.startswith(('"','#')):
   n=create(doc,ST,'Access',dict(attrs,Scope='LocalVariable' if t.startswith('#') else 'GlobalVariable'));sym=create(doc,ST,'Symbol');n.appendChild(sym)
   for i,part in enumerate(t.lstrip('#').split('.')):
    if i:sym.appendChild(create(doc,ST,'Token',{'Text':'.'}))
    sym.appendChild(create(doc,ST,'Component',{'Name':part.strip('"')}))
  elif t in ('TRUE','FALSE') or t.isdigit():
   n=create(doc,ST,'Access',dict(attrs,Scope='LiteralConstant'));c=create(doc,ST,'Constant');n.appendChild(c);c.appendChild(create(doc,ST,'ConstantValue',text=t))
  else:n=create(doc,ST,'Token',dict(attrs,Text=t))
  nodes.append(n)
 return nodes

def load(name):return D.parse(str(CAP/(name+'.xml')))
def save(doc,name):
 (GEN/(name+'.xml')).write_text(doc.toxml(),encoding='utf-8')
def unit(doc,ident):return next(x for x in doc.getElementsByTagName('SW.Blocks.CompileUnit') if x.getAttribute('ID')==ident)
def st(doc,ident):return desc(unit(doc,ident),'StructuredText')[0]
def insert_code(doc,node,code,before=None):
 for n in scl(doc,code):node.insertBefore(n,before) if before is not None else node.appendChild(n)
def access_path(n):return '.'.join(x.getAttribute('Name') for x in desc(n,'Component'))
# Append only new product state fields; preserve all existing offsets/order and metadata.
d=load('DB_OP275Product');section=next(x for x in desc(d,'Section') if x.getAttribute('Name')=='Static');ns=section.namespaceURI
for name,comment in [('LeakRetestUsed','本件已消耗唯一一次自动复测机会，仅新件/清件后清零'),('LeakRetestRequested','首次有效NG待重新封堵，不能发布为最终结果'),('FirstLeakNG','保留本件第一次气密NG记录，复测OK也不抹去'),('LeakRetestFailed','唯一一次复测仍NG，最终报警停站')]:
 assert not any(x.getAttribute('Name')==name for x in elems(section,'Member'))
 member=create(d,ns,'Member',{'Name':name,'Datatype':'Bool','Remanence':'NonRetain','Accessibility':'Public'})
 al=create(d,ns,'AttributeList');member.appendChild(al)
 for key in ['ExternalAccessible','ExternalVisible','ExternalWritable']:al.appendChild(create(d,ns,'BooleanAttribute',{'Name':key,'SystemDefined':'true'},'false' if key=='ExternalWritable' else 'true'))
 co=create(d,ns,'Comment');co.appendChild(create(d,ns,'MultiLanguageText',{'Lang':'zh-CN'},comment));member.appendChild(co);section.appendChild(member)
member=create(d,ns,'Member',{'Name':'RetestStopMinimum','Datatype':'Time','Remanence':'NonRetain','Accessibility':'Public'})
al=create(d,ns,'AttributeList');member.appendChild(al)
for key in ['ExternalAccessible','ExternalVisible','ExternalWritable']:al.appendChild(create(d,ns,'BooleanAttribute',{'Name':key,'SystemDefined':'true'},'false' if key=='ExternalWritable' else 'true'))
member.appendChild(create(d,ns,'StartValue',text='T#2S'))
co=create(d,ns,'Comment');co.appendChild(create(d,ns,'MultiLanguageText',{'Lang':'zh-CN'},'重新封堵前最小停止等待，工程固定2秒，不作为压力已释放的证明'));member.appendChild(co);section.appendChild(member)
save(d,'DB_OP275Product')
P='"DB_OP275Product"'; B='"DB_OP275BGraph"'; C='"DB_OP275Cylinder"'; R='"DB_OP275Branch"'; SIG='"DB_OP275BasicSign"'; COS='"OP275_Comso_DB"'
reset=f'''\nIF NOT {P}.ActiveValid THEN
 {P}.LeakRetestUsed := FALSE;
 {P}.LeakRetestRequested := FALSE;
 {P}.FirstLeakNG := FALSE;
 {P}.LeakRetestFailed := FALSE;
END_IF;\n'''
request=f'''\nIF {B}.LatchResult AND {P}.ActiveValid AND {COS}.FreshEnd
 AND NOT {P}.Result.Valid AND NOT {P}.LeakRetestUsed
 AND {R}.IO.LeakNG AND NOT {R}.IO.LeakOK AND NOT {COS}.InvalidResult THEN
 {P}.LeakRetestRequested := TRUE;
 {P}.FirstLeakNG := TRUE;
END_IF;\n'''
prepare=f'''\nIF ({B}.OP275自动流程工步 = 70) AND {P}.LeakRetestRequested THEN
 {P}.LeakRetestUsed := TRUE;
END_IF;
IF ({B}.OP275自动流程工步 = 73) AND {P}.LeakRetestUsed
 AND {C}.Z04.Wpos_SE AND {C}.Z05.Hpos_SE AND {C}.Z07.Hpos_SE
 AND NOT {R}.IO.LeakRunning THEN
 {P}.Result.Valid := FALSE;
 {P}.Result.OK := FALSE;
 {P}.Result.NG := FALSE;
 {P}.Result.Invalid := FALSE;
 {P}.LeakRetestRequested := FALSE;
END_IF;
{P}.LeakRetestFailed := ({B}.OP275自动流程工步 = 51)
 AND {P}.LeakRetestUsed AND {P}.Result.Valid AND {P}.Result.NG
 AND NOT {P}.Result.OK AND NOT {P}.Result.Invalid;\n'''
d=load('OP275_ProductInfo');n=st(d,'3');children=elems(n)
access=next(x for x in children if x.localName=='Access' and access_path(x)=='DB_OP275BGraph.LatchResult');idx=children.index(access)
ifnode=next(x for x in reversed(children[:idx]) if x.localName=='Token' and x.getAttribute('Text')=='IF')
then=next(x for x in children[idx:] if x.localName=='Token' and x.getAttribute('Text')=='THEN')
insert_code(d,n,request,ifnode);insert_code(d,n,f' AND NOT {P}.LeakRetestRequested ',then)
insert_code(d,n,reset,n.firstChild);insert_code(d,n,prepare)
save(d,'OP275_ProductInfo')
# Fault recovery on the new steps retains the consumed retry and requires a valid clamped part.
recovery=f'''\nIF (#step >= 70) AND (#step <= 73) THEN
 #eligible := {P}.ActiveValid AND {P}.LeakRetestUsed
 AND {R}.IO.PartPresent AND {R}.IO.LevelLeft AND {R}.IO.UpperWork
 AND #clear AND NOT {R}.IO.LeakRunning;
END_IF;\n'''
d=load('OP275_BranchControl');n=st(d,'3');target=next(x for x in elems(n) if x.localName=='Access' and access_path(x)=='DB_OP275Branch.ResumeEligible');insert_code(d,n,recovery,target);save(d,'OP275_BranchControl')
# Retest failures feed the existing leak alarm and station fault path.
d=load('OP275_Alarm');n=st(d,'8')
insert_code(d,n,f'''\n"DB_OP275Alarm".SensorConflict := "DB_OP275Alarm".SensorConflict
 OR (({B}.OP275自动流程工步 >= 70) AND ({B}.OP275自动流程工步 <= 73)
 AND (NOT {R}.IO.PartPresent OR NOT {R}.IO.LevelLeft OR NOT {R}.IO.UpperWork));\n''')
u=unit(d,'12');a=child(u,'AttributeList');src=child(a,'NetworkSource');
for x in list(src.childNodes):src.removeChild(x)
n=create(d,ST,'StructuredText',{'xmlns':ST});src.appendChild(n)
insert_code(d,n,f'"DB_OP275Alarm".LeakFault := {R}.IO.LeakFault OR {P}.LeakRetestFailed;\n')
lang=child(a,'ProgrammingLanguage');lang.firstChild.nodeValue='SCL'
for x in desc(u,'Text'):
 for c in list(x.childNodes):x.removeChild(c)
 x.appendChild(d.createTextNode('气密仪异常或重新封堵复测仍NG，进入本站故障汇总'))
save(d,'OP275_Alarm')
# Generate new GRAPH steps with original machinery permissions preserved.
m=json.loads((OUT/'graph-model.json').read_text(encoding='utf-8'))['model'];seq=m['sequences'][0]
def contact(symbol,neg=False):return dict(symbol=symbol,negated=neg)
def cmp(left,op,right,dt='Int'):return dict(left=left,compare=op,right=right,datatype=dt)
def allof(*items):return {'branches':[[contact(x) if isinstance(x,str) else x for x in items]]}
common=[contact(SIG+'.Command.RunInhibit',True),contact(SIG+'.Permit.Enabled'),contact(R+'.IO.SafetyRelayReady'),contact(R+'.GraphAdvancePermit')]
physical=[contact(SIG+'.Permit.RobotClear'),contact(P+'.ActiveValid'),contact(R+'.IO.PartPresent'),contact(R+'.IO.LevelLeft'),contact(C+'.Z04.Wpos_SE'),contact(R+'.IO.LeakRunning',True)]
for b in seq['branches']:
 if b['number']==6:b['cardinality']=4
names={70:'S70首次NG停止气密等待卸压',71:'S71复测侧封堵缩回',72:'S72复测顶部封堵缩回',73:'S73清除本次结果准备重新封堵'}
for number,name in names.items():
 actions=[{'qualifier':'N','text':B+'.OP275自动流程工步 := '+str(number)},{'qualifier':'N','text':COS+'.PLCToComso.Stop'},{'qualifier':'N','text':B+'.AutoActive'}]
 if number in (71,72):actions.append({'qualifier':'N','text':C+('.Z07' if number==71 else '.Z05')+'.Trigger_H_Auto'})
 seq['steps'].append(dict(number=number,name=name,initial=False,maximum_step_time='T#15S' if number==70 else 'T#10S',warning_time='T#12S' if number==70 else 'T#7S',actions=actions))
seq['transitions'].append(dict(number=224,name='T224首次有效NG只允许一次重新封堵',language='LAD',condition=allof(P+'.LeakRetestRequested',contact(P+'.LeakRetestUsed',True),contact(P+'.Result.Valid',True),*common)))
seq['transitions'].append(dict(number=270,name='T270仪器停止且等待完成才退封堵',language='LAD',condition=allof(P+'.LeakRetestUsed',cmp('#'+names[70]+'.T','>=',P+'.RetestStopMinimum','Time'),cmp('#'+names[70]+'.T','>=','"DB_OP275Config".ExhaustDelay','Time'),*physical,*common)))
seq['transitions'].append(dict(number=271,name='T271侧封堵退回到位再退顶部',language='LAD',condition=allof(C+'.Z07.Hpos_SE',C+'.Z05.Wpos_SE',*physical,*common)))
seq['transitions'].append(dict(number=272,name='T272顶部侧面均退回到位',language='LAD',condition=allof(C+'.Z05.Hpos_SE',C+'.Z07.Hpos_SE',*physical,*common)))
seq['transitions'].append(dict(number=273,name='T273旧结果清除后重新顶部侧面封堵',language='LAD',condition=allof(P+'.LeakRetestUsed',contact(P+'.LeakRetestRequested',True),contact(P+'.Result.Valid',True),C+'.Z05.Hpos_SE',C+'.Z07.Hpos_SE',*physical,*common)))
def link(f,n,t,m,fp=0,jump=False):return {'from':{'kind':f,'number':n,'port':fp},'to':{'kind':t,'number':m,'port':0},'link_type':'Jump' if jump else 'Direct'}
seq['connections'] += [link('Branch',6,'Transition',224,3),link('Transition',224,'Step',70)]
for n in range(70,74):seq['connections'].append(link('Step',n,'Transition',n+200));seq['connections'].append(link('Transition',n+200,'Step',n+1 if n<73 else 8,jump=n==73))
# Preserve raw XML for existing steps/transitions, including all unrelated sequences.
(GEN/'graph-model.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
(GEN/'added-logic.scl').write_text(reset+request+prepare+recovery,encoding='utf-8')
print('Prepared 4 supporting blocks and GRAPH model')
