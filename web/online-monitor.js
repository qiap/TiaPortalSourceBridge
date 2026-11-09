(() => {
  'use strict';const el=id=>document.getElementById(id),node=(tag,text)=>{const n=document.createElement(tag);n.textContent=text??'';return n;};
  let connection=null,busy=false,recording=false,timer=null,generation=0,archive=null,scope='';
  const target=()=>({project_path:window.currentStatus?.active_project||window.currentStatus?.project_path||'',plc_name:window.currentStatus?.target_plc||'',target_token:window.currentStatus?.target_token||''});
  const key=t=>JSON.stringify([t.project_path,t.plc_name]);
  const message=text=>el('monitorStatus').textContent=text;
  function sync(){
    const t=target();el('monitorTarget').textContent=[t.project_path,t.plc_name].join(' · ');
    if(scope!==key(t)){scope=key(t);generation++;clearTimeout(timer);archive=null;el('monitorValues').replaceChildren();el('monitorWaveforms').replaceChildren();el('monitorArchive').textContent='';if(connection)message('工程已切换。请先断开原监控连接，再配置当前目标。');}
    for(const id of ['monitorIp','monitorRack','monitorSlot','monitorTransport','monitorMode','monitorTags'])el(id).disabled=busy||!!connection;
    el('monitorConnect').disabled=busy||!!connection||!t.target_token;
    el('monitorDisconnect').disabled=busy||!connection;
    const blocked=busy||!connection||key(connection.target)!==key(t);
    for(const id of ['monitorRead','monitorSnapshot','monitorStart'])el(id).disabled=blocked||recording;
    el('monitorStop').disabled=blocked||!recording;el('monitorExport').disabled=blocked||recording;el('monitorDownloadSnapshot').disabled=!archive;
  }
  async function request(action,data={},binding=connection){
    const response=await fetch('/api/io/monitor/'+action,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...binding?.target,...(binding?{}:target()),session_id:binding?.id,...data})});
    if(action==='export'&&response.ok)return response.blob();const result=await response.json();if(!response.ok)throw Error(result.error||'请求失败');return result;
  }
  async function work(label,fn){sync();if(busy)return;busy=true;sync();message(label);const ticket=generation;try{await fn(()=>ticket===generation);}catch(e){if(ticket===generation)message('操作失败：'+e.message);}finally{busy=false;sync();}}
  function showValues(sample){window.dispatchEvent(new CustomEvent("bridge-monitor-sample",{detail:{target:connection?.target,sample}}));const table=document.createElement('table');table.style.width='100%';const head=table.createTHead().insertRow();['变量','值','质量','读取时间（UTC）','地址'].forEach(s=>head.append(node('th',s)));const body=table.createTBody();for(const [name,value] of Object.entries(sample.values||{})){const row=body.insertRow();[name,value.quality==='good'?String(value.value):'未知',value.quality,value.timestamp_utc,value.address||value.error].forEach(s=>row.append(node('td',s)));}el('monitorValues').replaceChildren(table);}
  function download(blob,name){const url=URL.createObjectURL(blob),a=node('a','');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
  el('monitorConnect').onclick=()=>work('正在建立只读通信…',async valid=>{
    const selected=target(),tags=el('monitorTags').value.split(/\r?\n/).filter(l=>l.trim()).map(line=>{const parts=line.split('|').map(s=>s.trim());if(parts.length!==3||parts.some(p=>!p))throw Error('每行需填写：符号名称 | 地址 | 类型');return{Name:parts[0],Address:parts[1],Type:parts[2],TestWritable:false};});
    const result=await request('connect',{profile:{Name:'Web monitor',Ip:el('monitorIp').value.trim(),Rack:Number(el('monitorRack').value),Slot:Number(el('monitorSlot').value),Tags:tags},transport:el('monitorTransport').value,target_mode:el('monitorMode').value},null);
    // Keep a handle even if the selected project changed while connecting, so it can be closed.
    connection={id:result.session_id,target:selected};recording=false;if(valid())message('只读连接已建立，请读取已知变量核对地址。');
  });
  el('monitorDisconnect').onclick=()=>work('正在断开通信…',async valid=>{await request('disconnect');connection=null;recording=false;window.dispatchEvent(new CustomEvent("bridge-monitor-sample",{detail:null}));clearTimeout(timer);if(valid())message('监控连接已断开。');});
  el('monitorRead').onclick=()=>work('正在读取变量…',async valid=>{const sample=await request('read');if(valid()){showValues(sample);message('已读取一次；各组数据不保证属于同一扫描周期。');}});
  el('monitorSnapshot').onclick=()=>work('正在读取并归档参数…',async valid=>{const data=await request('snapshot');if(valid()){archive=data;showValues(data.sample);el('monitorArchive').textContent=JSON.stringify(data,null,2);if(el('sourceParameterId')){el('sourceParameterId').value=data.id;el('sourceParameterId').dispatchEvent(new Event('input'));}message('参数快照已归档。可在“变更审查与 Diff”选择源码快照并预览初始值回填，当前尚未修改工程。');}});
  el('monitorDownloadSnapshot').onclick=()=>{if(archive)download(new Blob([JSON.stringify(archive,null,2)],{type:'application/json'}),'TIA-Parameters-'+archive.id+'.json');};
  el('monitorExport').onclick=()=>work('正在导出完整变化记录…',async valid=>{const blob=await request('export');if(valid()){download(blob,'TIA-Trace.jsonl');message('已导出完整变化记录，包含质量及时间戳。');}});
  el('monitorStart').onclick=()=>work('正在开始录波…',async valid=>{await request('start',{interval_ms:Number(el('monitorInterval').value),duration_seconds:Number(el('monitorDuration').value)});recording=true;if(valid()){message('正在录波。');poll(generation,connection);}});
  el('monitorStop').onclick=()=>work('正在停止录波…',async valid=>{await request('stop');recording=false;clearTimeout(timer);if(valid()){await poll(generation,connection);message('录波已停止，可以导出完整记录。');}});
  async function poll(ticket,binding){
    if(ticket!==generation||binding!==connection)return;
    try{const data=await request('events',{},binding);if(ticket!==generation||binding!==connection)return;recording=!!data.watch.running;draw(data.events||[]);el('monitorTraceStatus').textContent='采样 '+data.watch.samples+' 次 / 变化及状态事件 '+data.watch.events+' 条；页面最近 500 条。可能遗漏短脉冲，不能当作逐扫描周期记录。';sync();if(recording)timer=setTimeout(()=>poll(ticket,binding),1000);}
    catch(e){if(ticket===generation){recording=false;message('采样状态读取失败：'+e.message+'。请停止或断开后核对。');sync();}}
  }
  function draw(events){
    const series=new Map();for(const event of events)for(const [name,row] of Object.entries(event.values||{})){if(!series.has(name))series.set(name,[]);series.get(name).push({time:Date.parse(row.timestamp_utc||event.timestamp_utc),value:row.quality==='good'?(typeof row.value==='boolean'?Number(row.value):Number(row.value)):null,quality:row.quality});}
    const box=el('monitorWaveforms');box.replaceChildren();const all=[...series.values()].flat().map(p=>p.time).filter(Number.isFinite);if(!all.length){box.append(node('p','尚无有效采样事件。'));return;}const start=Math.min(...all),end=Math.max(...all,start+1);
    for(const [name,points] of series){const values=points.filter(p=>p.value!==null&&Number.isFinite(p.value)).map(p=>p.value),min=Math.min(...values,0),max=Math.max(...values,min+1);box.append(node('p',name+' · 独立纵轴 '+min+'～'+max));
      const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 1000 90');svg.style.cssText='width:100%;height:100px;background:#101827';svg.setAttribute('role','img');svg.setAttribute('aria-label',name+' 采样变化曲线');
      let previous=null;for(const p of points){const x=10+980*(p.time-start)/(end-start);if(!Number.isFinite(x))continue;const good=p.value!==null&&Number.isFinite(p.value),y=good?75-60*(p.value-min)/(max-min):75;
        if(previous){const line=document.createElementNS(svg.namespaceURI,'path');line.setAttribute('d','M '+previous.x+' '+previous.y+' H '+x+(good&&previous.good?' V '+y:''));line.setAttribute('fill','none');line.setAttribute('stroke',previous.good?'#22c6a1':'#9ca3af');line.setAttribute('stroke-width','2');if(!previous.good)line.setAttribute('stroke-dasharray','5 4');svg.append(line);}previous={x,y,good};}
      box.append(svg);
    }
  }
  sync();setInterval(sync,750);
})();
