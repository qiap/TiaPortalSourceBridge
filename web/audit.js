/* Read-only diagnostics. Every request and report remains bound to its source snapshot. */
(() => {
  const el=id=>document.getElementById(id);
  if(!el('auditRun'))return;
  const rules={address_overlap:'地址重叠',multiple_writes:'多处写入',template_pin:'模板参数接线'};
  const severity={warning:'待核对',info:'提示'};
  const coverageNames={programs_total:'PLC 程序总数',programs_read:'已读取 PLC 程序',tag_tables_total:'变量表总数',tag_tables_read:'已读取变量表',blocks_total:'程序块总数',blocks_read:'已读取程序块',blocks_parsed:'已解析程序块',native_tags:'原始变量声明',addresses_checked:'已检查地址',templates:'有效项目模板',template_calls_checked:'已检查模板调用',supported_write_sites:'已识别写入位置',unsupported_blocks:'部分或全部行为未覆盖的块'};
  const sources={local_cache:'本地缓存源码（本次未重新读取博途）',tia_readback:'从博途读取的源码快照'};
  const accessNames={declaration:'变量声明',writes:'写入',reads_writes:'读取与写入',connection:'参数连接'};
  const pageSize=50;
  let sequence=0,busy=false,phase='',pending=null,report=null,reportContext=null,jobId='',page=1,exporting=false;
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
    renderFindings();sync();
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
    if(!filtered.length)el('auditFindings').append(text('p',report.findings.length?'没有符合当前筛选的结果。':'在当前覆盖范围内未发现这三类现象；请查看未覆盖项，不能据此认定程序安全。'));
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
  window.auditSync=sync;sync();setInterval(sync,750);
})();
