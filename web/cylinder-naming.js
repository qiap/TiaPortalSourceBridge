// Build a naming plan only. Applying the call requires the matching declaration renames.
window.CylinderNaming = (() => {
  const requireValue = (ok, message) => { if (!ok) throw new Error(message); };
  const quote = value => '"' + value.replaceAll('"','""') + '"';
  const splitSymbol = symbol => {
    const value=String(symbol || ''), parts=[]; let start=0, quoted=false, depth=0;
    for(let i=0;i<value.length;i++) {
      const c=value[i];
      if(c==='"') {
        if(quoted && value[i+1]==='"') {i++;continue;}
        quoted=!quoted;
      } else if(!quoted) {
        if(c==='[') depth++;
        else if(c===']') {depth--;requireValue(depth>=0,'点位符号的数组下标不完整');}
        else if(c==='.' && depth===0) {parts.push(value.slice(start,i));start=i+1;}
      }
    }
    requireValue(!quoted && depth===0,'点位符号的引号或数组下标不完整');
    parts.push(value.slice(start));
    requireValue(parts.every(p=>p.trim()),'点位符号缺少成员名称');
    return parts;
  };
  const unquote = token => {
    token=token.trim();
    if(token.startsWith('"')) {
      requireValue(/^"(?:[^"]|"")*"$/.test(token),'暂不支持重命名带切片或数组下标的点位成员');
      return token.slice(1,-1).replaceAll('""','"');
    }
    requireValue(/^[\p{L}_][\p{L}\p{N}_]*$/u.test(token),'暂不支持重命名带切片、数组下标或绝对地址的点位成员');
    return token;
  };
  const symbolKey = value => splitSymbol(value).map(p=>{
    try {return unquote(p).toLowerCase();} catch (_) {return p.trim().toLowerCase();}
  }).join('\u0001');
  const address = value => {
    const match=/^%?([IEQA])\s*(\d+)\.([0-7])$/i.exec(String(value || '').trim());
    requireValue(match && Number.isSafeInteger(+match[2]),'请选择有明确位地址的输入或输出点');
    return ({E:'I',A:'Q'}[match[1].toUpperCase()] || match[1].toUpperCase()) + String(+match[2]) + '.' + match[3];
  };
  const directions = {
    i_Wpos: {role:'input',suffixes:['伸出到位','伸出位'],fallback:'伸出到位'},
    i_Hpos: {role:'input',suffixes:['缩回到位','缩回位'],fallback:'缩回到位'},
    oOutput_W: {role:'output',suffixes:['伸出电磁阀','伸出阀','伸出输出'],fallback:'伸出阀'},
    oOutput_H: {role:'output',suffixes:['缩回电磁阀','缩回阀','缩回输出'],fallback:'缩回阀'}
  };
  const suffixFor = (pin, role) => {
    const expected=directions[pin.name];
    requireValue(!expected || expected.role===role,'参数 '+pin.name+' 的输入输出方向不一致');
    let leaf='';
    if(pin.binding?.parts?.length) leaf=pin.binding.parts.at(-1).name;
    else if(pin.binding?.value && !pin.binding.open) {
      try {leaf=unquote(splitSymbol(pin.binding.value).at(-1));} catch (_) { /* use a verified directional pin below */ }
    }
    const prefix=leaf.replace(/\s*%?[IEQA]\s*\d+\.[0-7]\s*$/i,'').trim();
    const possible=expected?.suffixes || Object.values(directions).filter(d=>d.role===role).flatMap(d=>d.suffixes);
    const suffix=possible.find(s=>prefix.endsWith(s));
    requireValue(suffix || expected,'无法从参数 '+pin.name+' 的样板名称识别伸出/缩回方向，请先选择具有明确方向命名的样板');
    return suffix || expected.fallback;
  };
  function plan(data, sample, rules, bindings, cylinderName) {
    const name=String(cylinderName || '').trim();
    requireValue(name.length>0,'请填写气缸名称，例如“侧面夹紧气缸”');
    requireValue([...name].length<=64 && !/[\u0000-\u001f\u007f"\\]/.test(name),'气缸名称请使用不超过 64 个字的单行文字，不包含双引号或反斜杠');
    const points=data.points.map(p=>({...p})), renames=[], seenAddresses=new Set(), names=new Set();
    for(const pin of sample.pins) {
      const role=rules[pin.name];
      if(role!=='input' && role!=='output') continue;
      const selected=bindings[pin.name];
      if(selected===undefined || selected===null || selected==='') continue; // Partial form preview.
      const point=points.find(p=>p.id===selected);
      requireValue(point,'所选点位已失效，请重新选择 '+pin.name);
      requireValue(point.availability==='unused','所选点位尚未核实为空闲：'+point.address);
      const physical=address(point.address);
      requireValue(physical[0]===(role==='input'?'I':'Q'),'参数 '+pin.name+' 的点位方向不匹配');
      requireValue(!seenAddresses.has(physical),'多个参数选择了同一地址 '+physical);seenAddresses.add(physical);
      const path=splitSymbol(point.symbol), oldName=unquote(path.at(-1));
      const newName=name+suffixFor(pin,role)+physical;
      requireValue([...newName].length<=128,'生成的点位名称过长，请缩短气缸名称');
      const newSymbol=[...path.slice(0,-1),quote(newName)].join('.'), key=symbolKey(newSymbol);
      requireValue(!names.has(key),'生成的点位名称重复：'+newName);
      requireValue(!data.points.some(p=>p.id!==selected && symbolKey(p.symbol)===key),'工程中已存在点位名称：'+newName);
      names.add(key);
      renames.push({id:point.id,pin:pin.name,address:physical,old_symbol:point.symbol,new_symbol:newSymbol,
        old_name:oldName,new_name:newName,kind:path.length===1?'plc_tag':'mapped_member'});
      point.symbol=newSymbol;
    }
    return {points,renames,warnings:renames.length ? ['点位名称已生成。应用调用草稿前，请一并应用点位重命名清单到对应映射 DB 或变量表，并更新映射引用。'] : []};
  }
  return {plan};
})();
