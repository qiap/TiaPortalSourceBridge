/* Read-only diagnostics. Every request and report remains bound to its source snapshot. */
(() => {
  const el=id=>document.getElementById(id);
  if(!el('auditRun'))return;
  const rules={address_overlap:'地址重叠',multiple_writes:'多处写入',template_pin:'模板参数接线',instance_reuse:'实例多处调用',no_ob_path:'未发现 OB 调用路径'};
  const severity={warning:'待核对',info:'提示'};
  const coverageNames={programs_total:'PLC 程序总数',programs_read:'已读取 PLC 程序',tag_tables_total:'变量表总数',tag_tables_read:'已读取变量表',blocks_total:'程序块总数',blocks_read:'已读取程序块',blocks_parsed:'已解析程序块',native_tags:'原始变量声明',addresses_checked:'已检查地址',templates:'有效项目模板',template_calls_checked:'已检查模板调用',supported_write_sites:'已识别写入位置',unsupported_blocks:'部分或全部行为未覆盖的块'};
  const sources={local_cache:'本地缓存源码（本次未重新读取博途）',tia_readback:'从博途读取的源码快照'};
  const accessNames={declaration:'变量声明',writes:'写入',reads_writes:'读取与写入',connection:'参数连接',calls:'调用',call:'调用',call_path:'调用路径',instance_call:'实例调用'};
  const graphStatuses={root:'OB 入口',reachable:'已找到 OB 路径',no_ob_path:'未发现 OB 路径',unknown:'无法判定'};
  const resolutions={resolved:'已定位',external:'当前快照外的目标',ambiguous:'目标有歧义',unresolved:'尚未定位'};
  const instanceStatuses={resolved:'实例身份已识别',unsupported:'实例身份未覆盖',none:'无状态实例'};
  const instanceScopes={global:'全局背景 DB',local:'宿主内多重实例',GlobalVariable:'全局背景 DB',LocalVariable:'宿主内多重实例'};
  const pageSize=50;
  let sequence=0,busy=false,phase='',pending=null,report=null,reportContext=null,jobId='',page=1,exporting=false;
  let graph=null,graphNodes=[],graphCalls=[],nodeMap=new Map(),nodeSearch=new Map(),callSearch=new Map(),selectedNodeId='',nodePage=1,callPage=1;
  const graphPageSize=25;
  const state=()=>window.cylinderIoState?.()||{id:'',target:{},busy:false};
  const normalized=value=>String(value||'').replace(/\\/g,'/').toLowerCase();
  const sameTarget=(a,b)=>!!a&&!!b&&normalized(a.project_path)===normalized(b.project_path)&&a.plc_name===b.plc_name;
  const sameContext=(a,s)=>!!a&&a.id===s.id&&sameTarget(a.target,s.target);
  const copyContext=s=>({id:s.id,target:{...s.target}});
  const blocking=s=>s.busy||!!window.cylinderApplying||!!window.cylinderRecoveryPending;
  const message=value=>el('auditStatus').textContent=value;
  const text=(tag,value,className)=>{const node=document.createElement(tag);node.textContent=value??'';if(className)node.className=className;return node;};
  const date=value=>{const d=new Date(value);return value&&!Number.isNaN(d.getTime())?d.toLocaleString():'未提供';};
  function sync() {
    const s=state();
    el('auditTarget').textContent=[s.target.project_path,s.target.plc_name].filter(Boolean).join(' · ')||'请先选择工程与 PLC。';
    if(busy && pending && (!sameTarget(pending.target,s.target)||(phase==='scan'&&pending.id!==s.id))) {
      sequence++;busy=false;phase='';pending=null;
      message('目标或源码快照已变化，已停止接收旧体检结果。请检查当前工程的快照。');
    }
    const blocked=busy||blocking(s), selected=!!s.target.project_path&&!!s.target.plc_name&&!!s.target.target_token;
    el('auditCached').disabled=el('auditLive').disabled=blocked||!selected;
    el('auditRun').disabled=blocked||!selected||!s.id;
    const stale=!!report&&!sameContext(reportContext,s);
    el('auditStale').hidden=!stale;
    if(stale)el('auditStale').textContent='下方是旧工程或旧快照的报告，仅供查看；尚未检查当前目标。请重新体检后导出。';
    el('auditExport').disabled=busy||exporting||!report||stale;
    if(phase==='read'&&busy) {
      const detail=el('ioMessage')?.textContent;
      message('正在只读获取源码快照，完成后自动体检。'+(detail?'\n'+detail:''));
    }
  }
  async function json(url,options={}) {
    const response=await fetch(url,{cache:'no-store',...options});
    let data;try{data=await response.json();}catch(_){throw new Error('服务未返回有效结果，请检查后台是否已更新。');}
    if(!response.ok)throw new Error(data.error||'请求失败：'+response.status);
    return data;
  }
  async function read(mode) {
    const s=state();if(busy||blocking(s)||!s.target.target_token||!s.target.plc_name)return;
    const ticket=++sequence,selected={...s.target},previousSnapshot=s.id;busy=true;phase='read';pending=copyContext(s);sync();
    try {
      await window.ioRead(mode);
      if(ticket!==sequence)return;
      const fresh=state();
      if(!sameTarget(selected,fresh.target))throw new Error('读取期间工程或 PLC 已切换，请重新读取。');
      if(!fresh.id||fresh.busy)throw new Error(el('ioMessage')?.textContent||'没有获得可用的源码快照。');
      if(fresh.id===previousSnapshot)throw new Error('没有获得新的源码快照；未使用原有快照开始体检。'+(el('ioMessage')?.textContent||''));
      busy=false;phase='';pending=null;await run();
    }catch(error){if(ticket===sequence)message('无法开始体检：'+error.message);}
    finally{if(ticket===sequence){busy=false;phase='';pending=null;sync();}}
  }
  async function run() {
    const s=state();if(busy||blocking(s)||!s.id||!s.target.target_token)return;
    const ticket=++sequence,selected=copyContext(s);busy=true;phase='scan';pending=selected;sync();
    message('正在分析当前源码快照，不修改工程…');
    try {
      const started=await json('/api/io/audit-run',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:s.id,...s.target})});
      if(ticket!==sequence||!sameContext(selected,state()))return;
      if(!started.id)throw new Error('体检任务编号缺失。');
      while(ticket===sequence&&sameContext(selected,state())) {
        const job=await json('/api/io/audit-status?'+new URLSearchParams({id:started.id}));
        if(ticket!==sequence||!sameContext(selected,state()))return;
        if(job.id!==started.id||!sameTarget({project_path:job.project_path,plc_name:job.plc_name},selected.target))throw new Error('体检结果与所选工程不一致，已停止显示。');
        if(job.state==='failed')throw new Error(job.error||job.message||'分析失败。');
        if(job.state==='completed') {
          const result=job.result;
          if(!result||!Array.isArray(result.findings)||!sameTarget(result,selected.target))throw new Error('报告缺失或工程身份不一致。');
          report=result;reportContext=selected;jobId=started.id;page=1;
          renderReport();message('体检完成。请结合定位证据核对结果；本次未修改工程。');break;
        }
        if(job.state!=='running')throw new Error('无法识别体检任务状态。');
        message(job.message||'正在分析源码快照…');
        await new Promise(resolve=>setTimeout(resolve,750));
      }
    }catch(error){if(ticket===sequence)message('体检未完成：'+error.message+' 可重新检查当前快照。');}
    finally{if(ticket===sequence){busy=false;phase='';pending=null;sync();}}
  }
  function renderReport() {
    el('auditReport').hidden=false;
    el('auditReportTarget').textContent=[report.project_path,report.plc_name].filter(Boolean).join(' · ');
    const coverage=report.coverage||{};
    el('auditSource').textContent='数据来源：'+(sources[coverage.source_mode]||coverage.source_mode||'未提供，请核对源码来源。');
    el('auditSourceNote').textContent='源码读取范围：'+(coverage.source_note||'未提供原始源码读取时间或范围，请结合源码缓存核对。');
    el('auditSnapshot').textContent='本次读取/整理时间：'+date(report.snapshot_started)+' 至 '+date(report.snapshot_completed)+' · 报告生成：'+date(report.generated_at);
    el('auditCoverage').replaceChildren(...Object.keys(coverageNames).filter(key=>coverage[key]!=null).map(key=>text('span',coverageNames[key]+'：'+coverage[key])));
    const notes=[...(Array.isArray(report.warnings)?report.warnings:[]),...(Array.isArray(coverage.limitations)?coverage.limitations:[])];
    if(coverage.truncated)notes.unshift('结果或证据达到数量上限，本报告不完整；请结合工程源码进一步核对。');
    if(!notes.length)notes.push('分析仅覆盖当前已读取且受支持的源码结构；不证明程序逻辑安全。');
    const uniqueNotes=[...new Set(notes)];
    el('auditLimitList').replaceChildren(...uniqueNotes.map(note=>text('li',note)));
    el('auditLimitsSummary').textContent='检查范围与未覆盖项 · '+uniqueNotes.length+' 条说明（点击查看）';
    el('auditLimits').open=false;
    const counts=report.findings.reduce((a,f)=>(a[f.severity]=(a[f.severity]||0)+1,a),{});
    el('auditSummary').textContent='共 '+report.findings.length+' 项发现 · 待核对 '+(counts.warning||0)+' 项 · 提示 '+(counts.info||0)+' 项';
    renderFindings();prepareGraph();showView('findings');sync();
  }
  function renderFindings() {
    if(!report)return;
    const query=el('auditSearch').value.trim().toLocaleLowerCase(),level=el('auditSeverity').value,rule=el('auditRule').value;
    const filtered=report.findings.filter(f=>(level==='all'||f.severity===level)&&(rule==='all'||f.rule===rule)&&(!query||JSON.stringify(f).toLocaleLowerCase().includes(query)));
    const pages=Math.max(1,Math.ceil(filtered.length/pageSize));page=Math.max(1,Math.min(page,pages));
    el('auditFilterSummary').textContent='当前筛选 '+filtered.length+' / 全部 '+report.findings.length+' 项';
    el('auditPage').textContent='第 '+page+' / '+pages+' 页';el('auditPrev').disabled=page<=1;el('auditNext').disabled=page>=pages;
    const nodes=filtered.slice((page-1)*pageSize,page*pageSize).map(f=>{
      const card=document.createElement('article');card.className='audit-finding';
      const heading=text('h4',''),badge=text('span',severity[f.severity]||f.severity,'badge '+(f.severity==='warning'?'badge-amber':'badge-blue'));
      heading.append(badge,text('span',f.title||rules[f.rule]||'检查结果'));card.append(heading);
      card.append(text('p',[rules[f.rule]||f.rule,f.subject].filter(Boolean).join(' · ')),text('p',f.explanation));
      if(f.suggestion)card.append(text('p','建议：'+f.suggestion));
      const detail=document.createElement('details'),locations=Array.isArray(f.locations)?f.locations:[];
      detail.append(text('summary','查看 '+locations.length+' 条定位证据'+(f.confidence==='confirmed'?' · 现象已确认':f.confidence==='review'?' · 需结合上下文判断':'')));
      const wrapper=document.createElement('div');wrapper.className='audit-evidence';const table=document.createElement('table');table.className='execution-table';
      const head=table.createTHead().insertRow();['程序块 / 位置','符号 / 地址','访问类型','证据'].forEach(value=>head.append(text('th',value)));
      for(const l of locations){const row=table.insertRow();[[l.unit,l.group,l.block,l.network?'网络 '+l.network:'',l.location].filter(Boolean).join(' / '),[l.symbol,l.address].filter(Boolean).join(' · '),accessNames[l.access]||l.access,l.evidence].forEach(value=>row.append(text('td',value)));}
      wrapper.append(table);detail.append(wrapper);card.append(detail);return card;
    });
    el('auditFindings').replaceChildren(...nodes);
    if(!filtered.length)el('auditFindings').append(text('p',report.findings.length?'没有符合当前筛选的结果。':'在当前覆盖范围内未发现所检查的现象；请查看未覆盖项，不能据此认定程序安全。'));
  }
  function showView(view) {
    const calls=view==='graph';
    el('auditFindingsPanel').hidden=calls;el('auditGraphPanel').hidden=!calls;
    el('auditFindingsView').setAttribute('aria-pressed',String(!calls));el('auditGraphView').setAttribute('aria-pressed',String(calls));
  }
  const nodeLabel=node=>node?[node.name,node.group,node.unit?'单元 '+node.unit:''].filter(Boolean).join(' / '):'';
  const graphText=value=>String(value??'').toLocaleLowerCase();
  function nodeLink(id,fallback) {
    const node=nodeMap.get(id);
    if(!node)return text('span',fallback||id||'未识别');
    const button=text('button',nodeLabel(node),'btn btn-sm');button.type='button';button.dataset.nodeId=id;button.onclick=()=>selectNode(id);return button;
  }
  function prepareGraph() {
    graph=report?.call_graph&&Array.isArray(report.call_graph.nodes)&&Array.isArray(report.call_graph.calls)?report.call_graph:null;
    graphNodes=graph?graph.nodes.filter(n=>n&&typeof n.id==='string'):[];
    nodeMap=new Map(graphNodes.map(n=>[n.id,n]));
    graphCalls=graph?graph.calls.filter(c=>c&&typeof c.caller_id==='string'):[];
    nodeSearch=new Map(graphNodes.map(n=>[n,graphText([n.name,n.group,n.unit,n.kind].join(' '))]));
    callSearch=new Map(graphCalls.map(c=>[c,graphText([nodeLabel(nodeMap.get(c.caller_id)),nodeLabel(nodeMap.get(c.callee_id)),c.callee_name,c.instance,c.instance_scope,JSON.stringify(c.location||{})].join(' '))]));
    selectedNodeId='';nodePage=1;callPage=1;
    for(const id of ['auditGraphNodeSearch','auditGraphCallSearch'])el(id).value='';
    for(const id of ['auditGraphNodeStatus','auditGraphDirection','auditGraphResolution'])el(id).value='all';
    el('auditGraphContent').hidden=!graph;
    el('auditGraphSelected').hidden=true;
    if(!graph) {
      el('auditGraphSummary').textContent='这份报告未包含调用关系数据，请重新检查当前快照。';
      el('auditGraphBoundary').textContent='旧报告仍可查看和导出；缺少调用数据不代表工程没有调用。';
      el('auditGraphLimits').hidden=true;return;
    }
    el('auditGraphSummary').textContent='程序块 '+graphNodes.length+' 个 · OB 入口 '+graphNodes.filter(n=>n.status==='root').length+' 个 · 已记录调用 '+graphCalls.length+' / '+(graph.calls_total??graphCalls.length)+' 处 · 已定位目标 '+(graph.calls_resolved??0)+' 处 · 已识别状态实例 '+(graph.instances_resolved??0)+' 处';
    el('auditGraphBoundary').textContent=(graph.partial?'调用关系覆盖不完整。':'基于当前可解析的显式调用。')+(graph.truncated?'节点、调用或路径达到上限，结果已截断。':'')+'路径表示源码中存在调用关系，不代表运行时一定执行；未发现 OB 路径不等于死代码。';
    const notes=[...new Set(Array.isArray(graph.limitations)?graph.limitations:[])];
    el('auditGraphLimitList').replaceChildren(...notes.map(note=>text('li',note)));
    el('auditGraphLimits').hidden=!notes.length;el('auditGraphLimits').open=false;
    el('auditGraphLimitsSummary').textContent='调用分析说明 · '+notes.length+' 条（点击查看）';
    renderGraphNodes();renderGraphSelected();renderGraphCalls();
  }
  function renderGraphNodes() {
    if(!graph)return;
    const query=graphText(el('auditGraphNodeSearch').value.trim()),status=el('auditGraphNodeStatus').value;
    const filtered=graphNodes.filter(n=>(status==='all'||n.status===status)&&(!query||nodeSearch.get(n).includes(query)));
    const pages=Math.max(1,Math.ceil(filtered.length/graphPageSize));nodePage=Math.max(1,Math.min(nodePage,pages));
    el('auditGraphNodeSummary').textContent='当前筛选 '+filtered.length+' / 已记录 '+graphNodes.length+' 个程序块';
    el('auditGraphNodePage').textContent='第 '+nodePage+' / '+pages+' 页';el('auditGraphNodePrev').disabled=nodePage<=1;el('auditGraphNodeNext').disabled=nodePage>=pages;
    const rows=filtered.slice((nodePage-1)*graphPageSize,nodePage*graphPageSize).map(n=>{
      const row=document.createElement('tr');row.dataset.selected=String(n.id===selectedNodeId);
      row.append(text('td',nodeLabel(n)),text('td',n.kind),text('td',graphStatuses[n.status]||n.status||'无法判定'));
      const operation=text('td',''),button=text('button',n.id===selectedNodeId?'已选择':'查看路径与调用','btn btn-sm');button.dataset.nodeId=n.id;button.onclick=()=>selectNode(n.id);operation.append(button);row.append(operation);return row;
    });
    el('auditGraphNodes').replaceChildren(...rows);
    if(!filtered.length){const row=document.createElement('tr'),cell=text('td','没有符合筛选条件的程序块。');cell.colSpan=4;row.append(cell);el('auditGraphNodes').append(row);}
  }
  function selectNode(id) {
    if(!nodeMap.has(id))return;
    selectedNodeId=id;callPage=1;el('auditGraphDirection').value='outgoing';
    el('auditGraphCallSearch').value='';el('auditGraphResolution').value='all';
    renderGraphNodes();renderGraphSelected();renderGraphCalls();
  }
  function renderGraphSelected() {
    const node=nodeMap.get(selectedNodeId);el('auditGraphSelected').hidden=!node;
    el('auditGraphDirection').querySelectorAll('option').forEach(option=>{if(option.value!=='all')option.disabled=!node;});
    if(!node)return;
    el('auditGraphSelectedTitle').textContent=nodeLabel(node);
    el('auditGraphSelectedNote').textContent=(graphStatuses[node.status]||'无法判定')+(node.note?' · '+node.note:'');
    const ids=Array.isArray(node.path)?node.path:[],path=[];
    if(ids.length) {
      path.push(text('span','一条已识别的 OB 路径：'));
      ids.slice(0,256).forEach((id,index)=>{if(index)path.push(text('span','→'));path.push(nodeLink(id,'快照中缺少节点：'+id));});
      if(ids.length>256)path.push(text('span','路径显示达到上限，其余节点省略。'));
    } else path.push(text('span',node.status==='root'?'此块本身是 OB 入口。':'当前报告未提供可确认的 OB 路径；请结合未覆盖项核对。'));
    el('auditGraphPath').replaceChildren(...path);
    el('auditGraphSelectedCounts').textContent='当前记录：调用其他块 '+graphCalls.filter(c=>c.caller_id===node.id).length+' 处 · 被其他块调用 '+graphCalls.filter(c=>c.callee_id===node.id).length+' 处';
  }
  function renderGraphCalls() {
    if(!graph)return;
    const query=graphText(el('auditGraphCallSearch').value.trim()),direction=el('auditGraphDirection').value,resolution=el('auditGraphResolution').value;
    const filtered=graphCalls.filter(c=>{
      if(direction!=='all'&&(!selectedNodeId||(direction==='outgoing'?c.caller_id!==selectedNodeId:c.callee_id!==selectedNodeId)))return false;
      if(resolution!=='all'&&(resolution==='resolved'?c.resolution!=='resolved':c.resolution==='resolved'))return false;
      return !query||callSearch.get(c).includes(query);
    });
    const pages=Math.max(1,Math.ceil(filtered.length/graphPageSize));callPage=Math.max(1,Math.min(callPage,pages));
    const scope=direction==='all'?'全部调用':direction==='outgoing'?'所选块的下游调用':'所选块的上游调用';
    el('auditGraphCallSummary').textContent=scope+' · 当前筛选 '+filtered.length+' / 已记录 '+graphCalls.length+' 处';
    el('auditGraphCallPage').textContent='第 '+callPage+' / '+pages+' 页';el('auditGraphCallPrev').disabled=callPage<=1;el('auditGraphCallNext').disabled=callPage>=pages;
    const rows=filtered.slice((callPage-1)*graphPageSize,callPage*graphPageSize).map(c=>{
      const row=document.createElement('tr'),location=c.location||{},caller=text('td',''),callee=text('td',''),instance=text('td','');
      caller.append(nodeLink(c.caller_id,location.block||c.caller_id),text('p',[location.network?'网络 '+location.network:'',location.location].filter(Boolean).join(' · ')));
      callee.append(nodeLink(c.callee_id,c.callee_name||'未知目标'),text('p',[c.callee_type==='instruction'?'内置指令':c.callee_type,resolutions[c.resolution]||c.resolution||'尚未定位'].filter(Boolean).join(' · ')));
      instance.append(text('span',c.instance||'—'),text('p',[instanceScopes[c.instance_scope]||c.instance_scope,instanceStatuses[c.instance_status]||c.instance_status].filter(Boolean).join(' · ')));
      row.append(caller,callee,instance,text('td',location.evidence||'未提供额外证据'));return row;
    });
    el('auditGraphCalls').replaceChildren(...rows);
    if(!filtered.length){const row=document.createElement('tr'),cell=text('td','没有符合筛选条件的显式调用；请结合分析覆盖范围核对。');cell.colSpan=4;row.append(cell);el('auditGraphCalls').append(row);}
  }
  async function exportReport() {
    sync();if(el('auditExport').disabled)return;
    const selected=reportContext,id=jobId;exporting=true;sync();
    try {
      const response=await fetch('/api/io/audit-export?'+new URLSearchParams({id}),{cache:'no-store'});
      if(!response.ok){const error=await response.json();throw new Error(error.error||'报告导出失败。');}
      const blob=await response.blob();
      if(id!==jobId||!sameContext(selected,state()))throw new Error('导出期间工程或源码快照已变化，请重新体检。');
      const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='TIA-程序体检-'+new Date().toISOString().slice(0,10)+'.xlsx';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
      message('Excel 已导出，包含本次体检全部结果、证据和覆盖说明。');
    }catch(error){message('导出失败：'+error.message);}
    finally{exporting=false;sync();}
  }
  el('auditCached').onclick=()=>read('source');el('auditLive').onclick=()=>read('live');el('auditRun').onclick=run;el('auditExport').onclick=exportReport;
  ['auditSeverity','auditRule'].forEach(id=>el(id).onchange=()=>{page=1;renderFindings();});
  el('auditSearch').oninput=()=>{page=1;renderFindings();};
  el('auditPrev').onclick=()=>{page--;renderFindings();};el('auditNext').onclick=()=>{page++;renderFindings();};
  el('auditFindingsView').onclick=()=>showView('findings');el('auditGraphView').onclick=()=>showView('graph');
  el('auditGraphNodeSearch').oninput=()=>{nodePage=1;renderGraphNodes();};el('auditGraphNodeStatus').onchange=()=>{nodePage=1;renderGraphNodes();};
  el('auditGraphNodePrev').onclick=()=>{nodePage--;renderGraphNodes();};el('auditGraphNodeNext').onclick=()=>{nodePage++;renderGraphNodes();};
  el('auditGraphCallSearch').oninput=()=>{callPage=1;renderGraphCalls();};
  for(const id of ['auditGraphDirection','auditGraphResolution'])el(id).onchange=()=>{callPage=1;renderGraphCalls();};
  el('auditGraphCallPrev').onclick=()=>{callPage--;renderGraphCalls();};el('auditGraphCallNext').onclick=()=>{callPage++;renderGraphCalls();};
  el('auditGraphClear').onclick=()=>{selectedNodeId='';callPage=1;el('auditGraphDirection').value='all';el('auditGraphResolution').value='all';el('auditGraphCallSearch').value='';renderGraphNodes();renderGraphSelected();renderGraphCalls();};
  window.auditSync=sync;sync();setInterval(sync,750);
})();
