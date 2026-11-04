// Convert only unconditional, fully wired FB calls. Never reconstruct a logic network by guessing.
window.CylinderModel = (() => {
  const children = (node, tag) => Array.from(node?.children || []).filter(x => !tag || x.localName === tag);
  const descendants = (node, tag) => Array.from(node.getElementsByTagNameNS('*', tag));
  const attr = (n, k) => n?.getAttribute(k) || '';
  const requireValue = (ok, message) => { if (!ok) throw new Error(message); };
  const identifier = /^[\p{L}_][\p{L}\p{N}_]*$/u;
  const quote = s => '"' + s.replaceAll('"', '""') + '"';
  const component = s => identifier.test(s) ? s : quote(s);
  const equal = (a,b) => a.toLowerCase() === b.toLowerCase();
  const lookup = (map,key) => Object.keys(map || {}).find(k => equal(k,key));
  const path = parts => parts.map((p,i) => (i ? component(p.name) : quote(p.name)) +
    (p.indices.length ? '[' + p.indices.join(',') + ']' : '')).join('.');
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
    const declarations = Object.create(null), instances = new Set(), occupied = new Set();
    for (const section of children(sections,'Section')) for (const m of children(section,'Member')) {
      declarations[attr(m,'Name')] = {type:attr(m,'Datatype'), section:attr(section,'Name')};
    }
    const samples = [], skipped = [];
    for (const [i,u] of descendants(host,'SW.Blocks.CompileUnit').entries()) {
      const calls = descendants(u,'CallInfo');
      for (const c of calls) {
        const inst = children(c,'Instance')[0];
        if (attr(inst,'Scope') === 'LocalVariable') instances.add(attr(children(inst,'Component')[0],'Name').toLowerCase());
      }
      // Include directly wired outputs of other calls, even in unsupported networks.
      for (const net of descendants(u,'FlgNet')) {
        const parts = children(children(net,'Parts')[0]);
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
      try {
        const net = descendants(u,'FlgNet')[0], parts = children(children(net,'Parts')[0]), c = calls[0];
        const call = parts.find(p => p.localName === 'Call');
        requireValue(calls.length === 1 && attr(c,'BlockType') === 'FB' && call && children(call).length === 1 &&
          parts.every(p => p === call || p.localName === 'Access'), '含附加逻辑、触点或多个调用');
        requireValue(children(c).every(n => ['Instance','Parameter'].includes(n.localName)), '含不支持的调用选项');
        const inst = children(c,'Instance')[0], id = attr(children(inst,'Component')[0],'Name');
        const fb = attr(c,'Name'), declared = declarations[id];
        requireValue(attr(inst,'Scope') === 'LocalVariable' && children(inst).length === 1 && identifier.test(id) &&
          declared?.section === 'Static' && equal(declared.type.replace(/^"|"$/g,''),fb), '缺少可核对的 Static 多重实例声明');
        const pins = children(c,'Parameter').map(p => ({name:attr(p,'Name'), type:attr(p,'Type'), dir:attr(p,'Section')}));
        requireValue(pins.length && pins.every(p => ['Input','Output','InOut'].includes(p.dir)), '不支持的形式参数');
        let enabled = false;
        for (const w of children(children(net,'Wires')[0])) {
          const names = children(w,'NameCon'), n = names[0], pname = attr(n,'Name');
          requireValue(w.localName === 'Wire' && children(w).length === 2 && names.length === 1 && attr(n,'UId') === attr(call,'UId'), '含分支或无法还原的连线');
          if (pname === 'en' && children(w,'Powerrail').length === 1) { enabled = true; continue; }
          const p = pins.find(k => k.name === pname), ident = children(w,'IdentCon')[0];
          requireValue(p && !p.binding && ident, '含使能条件、开路或重复连接');
          p.binding = operand(parts.find(a => a.localName === 'Access' && attr(a,'UId') === attr(ident,'UId')));
        }
        requireValue(enabled && pins.every(p => p.binding), '使能或参数连接不完整');
        samples.push({id,fb,pins,network:i+1});
      } catch (e) { skipped.push('网络 ' + (i+1) + '：' + e.message); }
    }
    return {samples,skipped,declarations,instances,occupied};
  }
  function roles(pin, sample) {
    const options = [];
    if (pin.type.toLowerCase() === 'bool' && pin.dir !== 'InOut') options.push(pin.dir === 'Input' ? 'input' : 'output');
    if (pin.binding.parts.length >= 2 && equal(pin.binding.parts[1].name,sample.id) && !pin.binding.parts[1].indices.length) options.push('device');
    if (pin.binding.parts.filter(c => c.indices.length).length === 1) options.push('index');
    if (pin.dir === 'Input' && !options.includes('device')) options.push('shared');
    return options;
  }
  function generate(data, model, sample, rules, name, values, alarm) {
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
      if (rule === 'input' || rule === 'output') {
        const pt = data.points.find(x => x.id === values[p.name] && x.area === (rule === 'input' ? 'I' : 'Q'));
        requireValue(pt, '请选择 ' + p.name + ' 的' + (rule === 'input' ? '输入' : '输出') + '点');
        after = pt.symbol;
        if (rule === 'output') {
          requireValue(!addresses.has(pt.address.toUpperCase()), '两个阀输出选择了同一地址 ' + pt.address);
          addresses.add(pt.address.toUpperCase());
          requireValue(!data.points.some(q => q.area === 'Q' && equal(q.address,pt.address) && model.occupied.has(q.symbol.toLowerCase())), '地址已接到宿主的现有调用：' + pt.address);
          if (pt.writes.length) warnings.push(pt.address + ' 有写入引用：' + pt.writes.join('、') + '。请核对输出归属。');
        }
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
  return {extract,roles,generate};
})();
