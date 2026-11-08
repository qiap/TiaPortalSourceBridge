// Convert unconditional FB calls, preserving explicitly open pins. Never guess missing wiring.
window.CylinderModel = (() => {
  const children = (node, tag) => Array.from(node?.children || []).filter(x => !tag || x.localName === tag);
  const descendants = (node, tag) => Array.from(node?.getElementsByTagNameNS('*', tag) || []);
  const attr = (n, k) => n?.getAttribute(k) || '';
  const requireValue = (ok, message) => { if (!ok) throw new Error(message); };
  const identifier = /^[\p{L}_][\p{L}\p{N}_]*$/u;
  const quote = s => '"' + s.replaceAll('"', '""') + '"';
  const component = s => identifier.test(s) ? s : quote(s);
  const equal = (a,b) => a.toLowerCase() === b.toLowerCase();
  const lookup = (map,key) => Object.keys(map || {}).find(k => equal(k,key));
  const path = parts => parts.map((p,i) => (i ? component(p.name) : quote(p.name)) +
    (p.indices.length ? '[' + p.indices.join(',') + ']' : '')).join('.');
  const symbolKey = value => String(value || '').replace(/"([\p{L}_][\p{L}\p{N}_]*)"/gu,'$1').toLowerCase();
  function pointAddress(value) {
    const m=/^%?([IEQA])\s*(\d+)\.([0-7])$/i.exec(String(value || '').trim());
    if(!m || !Number.isSafeInteger(+m[2]) || +m[2] > 2147483647) return '';
    return ({E:'I',A:'Q'}[m[1].toUpperCase()] || m[1].toUpperCase()) + String(+m[2]) + '.' + m[3];
  }
  const pointNumber = p => {const a=pointAddress(p.address);return a ? +a.slice(1,-2)*8 + +a.slice(-1) : -1;};
  const bankKey = p => p.bank || p.mapping_group || p.group || p.table || '';
  function pointState(data, model, point) {
    const key=pointAddress(point.address);
    if(!key || key[0]!==point.area) return 'unknown';
    const aliases=data.points.filter(p=>pointAddress(p.address)===key);
    const used=aliases.some(p=>p.availability==='used' || model.hostSymbols?.has(symbolKey(p.symbol)) ||
      [...(model.occupied || [])].some(s=>symbolKey(s)===symbolKey(p.symbol)));
    if(used) return 'used';
    return aliases.every(p=>p.availability==='unused') ? 'unused' : 'unknown';
  }
  function availablePoints(data, model, sample, area) {
    const endpoints=(model.endpointSymbols || []).filter(e=>equal(e.fb,sample.fb));
    const endpointKeys=new Set(endpoints.map(e=>symbolKey(e.symbol)));
    const existing=data.points.filter(p=>p.area===area && pointAddress(p.address) && endpointKeys.has(symbolKey(p.symbol)));
    // An old snapshot/model can still establish a donor boundary, without treating shared inputs as cylinder endpoints.
    if(!existing.length) for(const pin of sample.pins || []) {
      if(!pin.binding?.open && ((pin.dir==='Input' && /pos|sense|sensor|feedback|到位/i.test(pin.name) && !/interlock|permit/i.test(pin.name)) || pin.dir==='Output'))
        existing.push(...data.points.filter(p=>p.area===area && symbolKey(p.symbol)===symbolKey(pin.binding.value)));
    }
    const banks=new Map();
    for(const point of existing) {const key=bankKey(point);if(key)banks.set(key,Math.max(banks.get(key) ?? -1,pointNumber(point)));}
    const anchor=existing.length ? Math.max(...existing.map(pointNumber)) : -1;
    const groups=new Map();
    for(const point of data.points) {
      if(point.area!==area || pointState(data,model,point)!=='unused')continue;
      const key=pointAddress(point.address);if(!groups.has(key))groups.set(key,[]);groups.get(key).push(point);
    }
    const rank = p => {
      const same=banks.has(bankKey(p)), end=same?banks.get(bankKey(p)):anchor;
      const after=end>=0 && pointNumber(p)>end;
      return {tier:same?(after?0:1):banks.size?2:after?0:1,end,after};
    };
    return [...groups.entries()].map(([key,aliases])=>{
      aliases.sort((a,b)=>rank(a).tier-rank(b).tier || Number(!!b.mapping_fc)-Number(!!a.mapping_fc) || String(a.symbol).localeCompare(String(b.symbol)));
      const point=aliases[0],r=rank(point),afterAddress=r.end<0?'':area+Math.floor(r.end/8)+'.'+r.end%8;
      return {...point,address_key:key,recommended:r.tier===0,after_address:afterAddress,
        recommendation:r.tier===0?'优先：已有气缸 '+afterAddress+' 之后':r.tier===1 && banks.size?'同组其他空闲点':'其他空闲点',_rank:r.tier};
    }).sort((a,b)=>a._rank-b._rank || pointNumber(a)-pointNumber(b) || a.symbol.localeCompare(b.symbol)).map(({_rank,...p})=>p);
  }
  function operand(access) {
    const scope = attr(access,'Scope');
    if (scope === 'LiteralConstant' || scope === 'TypedConstant') {
      const v = descendants(access,'ConstantValue')[0]?.textContent || '';
      requireValue(/^(true|false|-?\d+)$/i.test(v) && scope === 'LiteralConstant', '暂不支持此常量表达式');
      return { value:v, parts:[] };
    }
    const symbol = children(access,'Symbol')[0];
    requireValue(scope === 'GlobalVariable' && symbol && children(access).length === 1, '含局部变量、绝对地址或间接操作数');
    const parts = children(symbol).map(c => {
      const name = attr(c,'Name'), indices = [];
      requireValue(c.localName === 'Component' && name && !/[\r\n"]/.test(name), '含不支持的符号成员');
      const modifier = attr(c,'AccessModifier');
      requireValue(!modifier || modifier === 'Array', '含不支持的访问修饰符');
      for (const index of children(c)) {
        const values = descendants(index,'ConstantValue');
        requireValue(modifier === 'Array' && index.localName === 'Access' && attr(index,'Scope') === 'LiteralConstant' &&
          values.length === 1 && /^-?\d+$/.test(values[0].textContent), '含动态数组下标');
        indices.push(values[0].textContent);
      }
      requireValue(!modifier || indices.length, '缺少数组下标');
      return {name, indices};
    });
    requireValue(parts.length, '没有可解析的符号');
    return {value:path(parts), parts};
  }
  function extract(data) {
    const doc = new DOMParser().parseFromString(data.xml, 'application/xml');
    requireValue(!doc.querySelector('parsererror'), '源块 XML 无法解析');
    const host = children(doc.documentElement,'SW.Blocks.FB')[0];
    requireValue(host, '请选择包含气缸多重实例的宿主 FB');
    const sections = descendants(children(host,'AttributeList')[0], 'Sections')[0];
    const declarations = Object.create(null), instances = new Set(), occupied = new Set(), hostSymbols = new Set(), endpointSymbols = [];
    for(const access of descendants(host,'Access')) {
      try { const binding=operand(access);if(binding.parts.length)hostSymbols.add(symbolKey(binding.value)); } catch(_) { /* Backend also checks absolute/indirect references. */ }
    }
    for (const section of children(sections,'Section')) for (const m of children(section,'Member')) {
      declarations[attr(m,'Name')] = {type:attr(m,'Datatype'), section:attr(section,'Name')};
    }
    const samples = [], candidates = [], skipped = [];
    for (const [i,u] of descendants(host,'SW.Blocks.CompileUnit').entries()) {
      const calls = descendants(u,'CallInfo');
      for (const c of calls) {
        const inst = children(c,'Instance')[0];
        if (attr(inst,'Scope') === 'LocalVariable') instances.add(attr(children(inst,'Component')[0],'Name').toLowerCase());
      }
      // Include directly wired outputs of other calls, even in unsupported networks.
      for (const net of descendants(u,'FlgNet')) {
        const parts = children(children(net,'Parts')[0]);
        const wires=children(children(net,'Wires')[0],'Wire');
        // Follow only endpoint-pin dependencies; public mode, reset and interlock signals do not set the allocation boundary.
        for(const target of parts.filter(p=>p.localName==='Call')) {
          const info=children(target,'CallInfo')[0];
          for(const formal of children(info,'Parameter')) {
            const pin=attr(formal,'Name'), dir=attr(formal,'Section');
            if(attr(formal,'Type').toLowerCase()!=='bool' || !(dir==='Output' || dir==='Input' && /pos|sense|sensor|feedback|到位/i.test(pin) && !/interlock|permit/i.test(pin)))continue;
            const visited=new Set();
            function collectWire(wire) {
              if(visited.has(wire))return;visited.add(wire);
              for(const connector of children(wire)) {
                const part=parts.find(p=>attr(p,'UId')===attr(connector,'UId'));
                if(connector.localName==='IdentCon' && part?.localName==='Access') {
                  try { const binding=operand(part);if(binding.parts.length)endpointSymbols.push({fb:attr(info,'Name'),pin,symbol:binding.value}); } catch(_) { }
                } else if(connector.localName==='NameCon' && part?.localName==='Part' && attr(connector,'Name')==='out') {
                  for(const upstream of wires.filter(w=>children(w,'NameCon').some(n=>attr(n,'UId')===attr(part,'UId') && attr(n,'Name')!=='out'))) collectWire(upstream);
                }
              }
            }
            wires.filter(w=>children(w,'NameCon').some(n=>attr(n,'UId')===attr(target,'UId') && attr(n,'Name')===pin)).forEach(collectWire);
          }
        }
        for (const w of children(children(net,'Wires')[0],'Wire')) {
          const access = parts.find(a => a.localName === 'Access' && attr(a,'UId') === attr(children(w,'IdentCon')[0],'UId'));
          if (!access) continue;
          for (const n of children(w,'NameCon')) {
            const call = parts.find(p => p.localName === 'Call' && attr(p,'UId') === attr(n,'UId'));
            const formal = children(children(call,'CallInfo')[0],'Parameter').find(p => attr(p,'Name') === attr(n,'Name'));
            if (['Output','InOut'].includes(attr(formal,'Section'))) {
              try { occupied.add(operand(access).value.toLowerCase()); } catch (_) { /* non-symbolic location */ }
            }
          }
        }
      }
      if (!calls.length) continue;
      for (const c of calls) {
        const inst = children(c,'Instance')[0], id = children(inst,'Component').map(n=>attr(n,'Name')).join('.') || '未命名实例';
        const fb = attr(c,'Name');
        const candidate = {id,fb,network:i+1,supported:false,reason:''};
        candidates.push(candidate);
        try {
        const nets = descendants(u,'FlgNet'), net = nets[0], parts = children(children(net,'Parts')[0]);
        const call = parts.find(p => p.localName === 'Call' && children(p,'CallInfo')[0] === c);
        requireValue(calls.length === 1, '同一网络含 '+calls.length+' 个调用，暂不能单独复用');
        requireValue(nets.length === 1 && call, '当前网络不是可解析的梯形图或功能块图调用');
        requireValue(attr(c,'BlockType') === 'FB', '当前调用不是 FB 多重实例');
        const logic = parts.filter(p => p !== call && p.localName !== 'Access');
        requireValue(!logic.length, '含 '+logic.length+' 个附加逻辑元件（'+[...new Set(logic.map(p=>attr(p,'Name') || p.localName))].join('、')+'），需保留原网络逻辑，暂不支持生成');
        requireValue(children(call).length === 1, '调用含附加修饰，暂不支持生成');
        requireValue(children(c).every(n => ['Instance','Parameter'].includes(n.localName)), '含不支持的调用选项');
        const declared = declarations[lookup(declarations,id)];
        requireValue(attr(inst,'Scope') === 'LocalVariable' && children(inst).length === 1 && identifier.test(id) &&
          declared?.section === 'Static' && equal(declared.type.replace(/^"|"$/g,''),fb), '缺少可核对的 Static 多重实例声明');
        const pins = children(c,'Parameter').map(p => ({name:attr(p,'Name'), type:attr(p,'Type'), dir:attr(p,'Section')}));
        requireValue(pins.length && pins.every(p => p.name && ['Input','Output','InOut'].includes(p.dir)), '不支持的形式参数');
        requireValue(new Set(pins.map(p=>p.name.toLowerCase())).size === pins.length, '形式参数名称重复');
        requireValue(new Set(parts.map(p=>attr(p,'UId'))).size === parts.length, '元件编号重复，无法核对参数接线');
        candidate.pins = pins;
        let enabled = false, enoSeen = false;
        for (const w of children(children(net,'Wires')[0])) {
          const names = children(w,'NameCon'), n = names[0], pname = attr(n,'Name');
          requireValue(w.localName === 'Wire' && children(w).length === 2 && names.length === 1 && attr(n,'UId') === attr(call,'UId'), '参数 '+(pname || '未知')+' 含分支或无法还原的连线');
          if (pname === 'en') {
            requireValue(!enabled, 'EN 使能重复连接');
            requireValue(children(w,'Powerrail').length === 1, 'EN 含使能条件，需保留原网络逻辑，暂不支持生成');
            enabled = true; continue;
          }
          if (pname === 'eno') {
            requireValue(!enoSeen, 'ENO 重复连接');
            requireValue(children(w,'OpenCon').length === 1, 'ENO 已连接到其他逻辑，暂不支持生成');
            enoSeen = true; continue;
          }
          const p = pins.find(k => k.name === pname), ident = children(w,'IdentCon')[0];
          requireValue(p, '连线指向未知参数 '+pname);
          requireValue(!p.binding, '参数 '+pname+' 重复连接');
          if (children(w,'OpenCon').length === 1) {
            requireValue(p.dir !== 'InOut', 'InOut 参数 '+pname+' 未连接，暂不能核实引用和初始值');
            p.binding = {value:'未连接',parts:[],open:true};
          } else {
            requireValue(ident, '参数 '+pname+' 含不支持的连线');
            const access = parts.find(a => a.localName === 'Access' && attr(a,'UId') === attr(ident,'UId'));
            requireValue(access, '参数 '+pname+' 的符号源未找到');
            try { p.binding = operand(access); } catch (e) { throw new Error('参数 '+pname+'：'+e.message); }
          }
        }
        requireValue(enabled, '缺少 EN 使能连线');
        const missing = pins.filter(p=>!p.binding).map(p=>p.name);
        requireValue(!missing.length, '缺少参数连线记录：'+missing.join('、')+'；不能视为未连接');
        candidate.supported = true;
        samples.push(candidate);
        } catch (e) {
          candidate.reason = e.message;
          skipped.push('网络 ' + (i+1) + ' / '+id+'：' + e.message);
        }
      }
    }
    return {samples,candidates,skipped,declarations,instances,occupied,hostSymbols,endpointSymbols};
  }
  const feedbackOutputs = (pin, sample) => pin.dir === 'Input' && pin.type.toLowerCase() === 'bool' && !pin.binding.open
    ? sample.pins.filter(p=>p.dir === 'Output' && p.type.toLowerCase() === 'bool' && p.binding && !p.binding.open && equal(p.binding.value,pin.binding.value)) : [];
  function roles(pin, sample) {
    if (pin.binding.open) return pin.dir === 'InOut' ? [] : ['open'];
    const options = [];
    const feedback = feedbackOutputs(pin,sample);
    if (feedback.length === 1) options.push('feedback');
    if (pin.type.toLowerCase() === 'bool' && pin.dir !== 'InOut') options.push(pin.dir === 'Input' ? 'input' : 'output');
    if (pin.binding.parts.length >= 2 && equal(pin.binding.parts[1].name,sample.id) && !pin.binding.parts[1].indices.length) options.push('device');
    if (pin.binding.parts.filter(c => c.indices.length).length === 1) options.push('index');
    if (pin.dir === 'Input' && !options.includes('device') && !feedback.length) options.push('shared');
    return options;
  }
  function generate(data, model, sample, rules, name, values, alarm) {
    requireValue(sample && sample.supported !== false, '当前实例暂不支持生成：'+(sample?.reason || '请选择可复用的样板'));
    requireValue(identifier.test(name) && name.length <= 64, '气缸名请使用不超过 64 位的字母、汉字、数字或下划线，首位不能是数字');
    requireValue(!model.instances.has(name.toLowerCase()), '宿主已调用同名实例：' + name);
    const declarations = [], warnings = [], diff = [], args = [], outputs = new Set(), addresses = new Set(), existing = lookup(model.declarations,name);
    const indexed = new Map(Object.entries(data.writes || {}).map(([k,v])=>[k.toLowerCase(),v]));
    if (existing) {
      const d = model.declarations[existing];
      requireValue(d.section === 'Static' && equal(d.type.replace(/^"|"$/g,''),sample.fb), '同名声明存在，但类型或区域不同');
      warnings.push('使用宿主中已声明的 Static 实例 ' + existing + '，请核对用途。');
    } else declarations.push('宿主 FB 的 Static：' + name + ' : ' + quote(sample.fb) + ';');
    for (const p of sample.pins) {
      const rule = rules[p.name], before = p.binding.value;
      requireValue(roles(p,sample).includes(rule), '请配置参数 ' + p.name);
      let after = before;
      if (rule === 'open') {
        diff.push({pin:p.name,before,after:'未连接（保持）',rule});
        warnings.push('参数 '+p.name+' 保持未连接，不在调用中赋值。'+(p.dir === 'Input' ? '新实例不会复制样板的当前值或定制初始值，请核对实例初始值及外部赋值。' : '该输出保留在实例数据中，不增加外部输出接线。'));
        continue;
      }
      if (rule === 'feedback') {
        const outputs = feedbackOutputs(p,sample), output = outputs[0];
        requireValue(outputs.length === 1 && rules[output.name] === 'output', '反馈参数 '+p.name+' 需要对应输出 '+(output?.name || '')+' 使用“选择输出点”');
        const pt = data.points.find(x=>x.id === values[output.name] && x.area === 'Q');
        requireValue(pt, '请先选择反馈参数 '+p.name+' 对应的输出点 '+output.name);
        after = pt.symbol;
      }
      if (rule === 'input' || rule === 'output') {
        const pt = data.points.find(x => x.id === values[p.name] && x.area === (rule === 'input' ? 'I' : 'Q'));
        requireValue(pt, '请选择 ' + p.name + ' 的' + (rule === 'input' ? '输入' : '输出') + '点');
        const state=pointState(data,model,pt),address=pointAddress(pt.address);
        requireValue(state==='unused', '点位 '+pt.address+(state==='used'?' 已使用或已接到现有程序，请选择新的空闲点':' 使用情况尚未核实，请重新读取工程'));
        requireValue(!addresses.has(address), '两个气缸参数选择了同一地址 '+pt.address);
        addresses.add(address);
        after = pt.symbol;
      } else if (rule === 'device' || rule === 'index') {
        const parts = p.binding.parts.map(c => ({name:c.name,indices:[...c.indices]}));
        const db = data.members[parts[0].name];
        if (rule === 'device') {
          const donor = db && lookup(db,sample.id);
          requireValue(donor && db[donor], '缺少样板 DB 成员类型，请补全索引：' + before);
          const member = lookup(db,name);
          requireValue(!member || equal(db[member], db[donor]), 'DB 中同名成员类型不一致：' + name);
          if (!member) declarations.push('DB ' + quote(parts[0].name) + '：' + name + ' : ' + db[donor] + ';');
          else warnings.push('复用已声明的数据成员 ' + quote(parts[0].name) + '.' + member + '，请核对用途。');
          parts[1].name = name;
        } else {
          requireValue(/^\d{1,9}$/.test(alarm), '请填写新的数组索引（非负整数）');
          const indexed = parts.find(c => c.indices.length);
          // Only simple top-level array declarations have a verifiable first dimension.
          const field = db && lookup(db,parts[1]?.name || '');
          const bounds = field && /^Array\s*\[\s*(-?\d+)\s*\.\.\s*(-?\d+)/i.exec(db[field]);
          requireValue(parts.indexOf(indexed) === 1 && bounds && +alarm >= +bounds[1] && +alarm <= +bounds[2], '数组第一维范围未核实或索引越界：' + before);
          indexed.indices[0] = String(+alarm);
        }
        after = path(parts);
        if (p.dir !== 'Input') {
          const key = after.replace(/"([\p{L}_][\p{L}\p{N}_]*)"/gu,'$1').toLowerCase();
          requireValue(!indexed.has(key), '写入目标已有索引记录：' + after + '（' + indexed.get(key) + '）');
        }
      }
      if (p.dir !== 'Input') {
        const key = after.toLowerCase();
        requireValue(!model.occupied.has(key) && !outputs.has(key), '写入目标已被现有调用或本草稿占用：' + after);
        outputs.add(key);
      }
      diff.push({pin:p.name,before,after,rule});
      args.push('    ' + component(p.name) + (p.dir === 'Output' ? ' => ' : ' := ') + after);
    }
    const code = '#' + name + '(\n' + args.join(',\n') + '\n);';
    return {code, declarations:[...new Set(declarations)], warnings:[...new Set(warnings)], diff};
  }
  return {extract,roles,generate,availablePoints,pointAddress};
})();
