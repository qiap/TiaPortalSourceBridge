using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class ContactEdit
    {
        public string compile_unit_id { get; set; }
        public int? sequence_index { get; set; }
        public string transition { get; set; }
        public string contact_uid { get; set; }
        public string scope { get; set; }
        public string[] components { get; set; }
        public bool? negated { get; set; }
    }
    internal sealed class LogicPlan
    {
        public int schema = 1;
        public string id, project_path, plc_name, block_name, group_path, created_utc;
        public string before_xml, after_xml, before_signature, after_signature;
        public ContactEdit edit;
    }
    // Only changes a selected Contact's operand and/or polarity. No XPath supplied by callers.
    internal static class RestrictedLogic
    {
        internal static ContactEdit ParseEdit(string json)
        {
            if(json==null||json.Length>16384)throw new ArgumentException("edit_json 过大。");
            var map=Json().DeserializeObject(json) as Dictionary<string,object>;
            var keys=new[]{"compile_unit_id","sequence_index","transition","contact_uid","scope","components","negated"};
            if(map==null||map.Keys.Any(k=>!keys.Contains(k)))throw new ArgumentException("edit_json 含未支持字段；不会静默忽略修改请求。");
            if(map.ContainsKey("negated")&&map["negated"]!=null&&!(map["negated"] is bool))throw new ArgumentException("negated 必须为 true/false。");
            if(map.ContainsKey("sequence_index")&&map["sequence_index"]!=null&&!(map["sequence_index"] is int))throw new ArgumentException("sequence_index 必须为整数。");
            return Json().Deserialize<ContactEdit>(json);
        }
        static XmlElement[] Children(XmlNode n) { return n == null ? new XmlElement[0] : n.ChildNodes.OfType<XmlElement>().ToArray(); }
        static XmlElement One(IEnumerable<XmlElement> rows, string what)
        { var a = rows.ToArray(); if (a.Length != 1) throw new ArgumentException("需要唯一的 " + what); return a[0]; }
        static string Attr(XmlElement n, string key) { return n.GetAttribute(key); }
        static void OnlyAttributes(XmlElement e, params string[] names)
        { if (e.Attributes.Cast<XmlAttribute>().Any(a => a.Prefix != "xmlns" && a.Name != "xmlns" && !names.Contains(a.Name))) throw new ArgumentException("不支持额外属性：" + e.LocalName); }
        internal static object Describe(string xml,ContactEdit edit)
        {
            var doc=BlockReadable.Parse(xml);
            XmlNode owner;
            if(edit.sequence_index.HasValue) {
                var seq=doc.SelectNodes("//*[local-name()='Graph']/*[local-name()='Sequence']").Cast<XmlNode>().ElementAt(edit.sequence_index.Value);
                owner=seq.SelectNodes("*[local-name()='Transitions']/*[local-name()='Transition']").OfType<XmlElement>().Single(e=>Attr(e,"Number")==edit.transition);
            }else owner=doc.SelectNodes("//SW.Blocks.CompileUnit").OfType<XmlElement>().Single(e=>Attr(e,"ID")==edit.compile_unit_id);
            var net=owner.SelectSingleNode(".//*[local-name()='FlgNet']");
            var contact=net.SelectNodes("*[local-name()='Parts']/*").OfType<XmlElement>().Single(e=>Attr(e,"UId")==edit.contact_uid);
            var wire=net.SelectNodes("*[local-name()='Wires']/*").OfType<XmlElement>().Single(w=>Children(w).Any(e=>e.LocalName=="NameCon"&&Attr(e,"UId")==edit.contact_uid&&Attr(e,"Name")=="operand"));
            string id=Children(wire).Single(e=>e.LocalName=="IdentCon").GetAttribute("UId");
            var access=net.SelectNodes("*[local-name()='Parts']/*").OfType<XmlElement>().Single(e=>Attr(e,"UId")==id);
            return new {scope=Attr(access,"Scope"),components=access.SelectNodes("*[local-name()='Symbol']/*").OfType<XmlElement>().Select(e=>Attr(e,"Name")).ToArray(),negated=Children(contact).Any(e=>e.LocalName=="Negated"),contact_uid=edit.contact_uid,compile_unit_id=edit.compile_unit_id,sequence_index=edit.sequence_index,transition=edit.transition};
        }
        internal static string Patch(string xml, ContactEdit edit)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.contact_uid)) throw new ArgumentException("必须指定 contact_uid。");
            if (edit.components == null && edit.negated == null) throw new ArgumentException("请指定新操作数或常闭状态。");
            if (edit.components == null && !string.IsNullOrEmpty(edit.scope)) throw new ArgumentException("scope 必须与 components 一起指定。");
            var doc = BlockReadable.Parse(xml);
            var block = One(Children(doc.DocumentElement).Where(e => new[] { "SW.Blocks.OB", "SW.Blocks.FB", "SW.Blocks.FC" }.Contains(e.LocalName)), "程序块");
            string language = block.SelectSingleNode("AttributeList/ProgrammingLanguage").InnerText;
            XmlElement net;
            if (language == "LAD") {
                if (edit.sequence_index.HasValue || !string.IsNullOrEmpty(edit.transition) || string.IsNullOrEmpty(edit.compile_unit_id)) throw new ArgumentException("LAD 必须仅指定 compile_unit_id。");
                var unit = One(block.SelectNodes("ObjectList/SW.Blocks.CompileUnit").OfType<XmlElement>().Where(e => Attr(e,"ID") == edit.compile_unit_id), "CompileUnit");
                net = One(unit.SelectNodes("AttributeList/NetworkSource/*").OfType<XmlElement>(), "LAD 网络");
            } else if (language == "GRAPH") {
                if (!string.IsNullOrEmpty(edit.compile_unit_id) || !edit.sequence_index.HasValue || edit.sequence_index.Value<0 || string.IsNullOrEmpty(edit.transition)) throw new ArgumentException("GRAPH 必须仅指定 sequence_index（从 0 开始）和 transition 编号。");
                GraphEditor.ValidateDocument(doc);
                var graph = One(doc.SelectNodes("//*[local-name()='Graph']").OfType<XmlElement>(), "Graph");
                var sequence = One(Children(graph).Where(e => e.LocalName == "Sequence").Skip(edit.sequence_index.Value).Take(1), "Sequence 序号");
                var transition = One(sequence.SelectNodes("*[local-name()='Transitions']/*[local-name()='Transition']").OfType<XmlElement>().Where(e => Attr(e,"Number") == edit.transition), "Transition 编号");
                if (Attr(transition,"ProgrammingLanguage") != "LAD") throw new ArgumentException("仅支持 LAD 转换条件。");
                net = One(Children(transition).Where(e => e.LocalName == "FlgNet"), "转换条件 FlgNet");
            } else throw new ArgumentException("受限修改只支持 LAD 块与 GRAPH 的 LAD 转换条件。");
            var allowedNamespaces=language=="GRAPH"?new[]{"http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v5"}:new[] { "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4", "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5" };
            if (net.LocalName != "FlgNet" || !allowedNamespaces.Contains(net.NamespaceURI)) throw new ArgumentException("未支持的网络格式。");
            var parts = Children(One(Children(net).Where(e => e.LocalName == "Parts"), "Parts"));
            var wires = Children(One(Children(net).Where(e => e.LocalName == "Wires"), "Wires"));
            var ids = parts.Select(e => Attr(e,"UId")).ToArray();
            if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("网络节点编号缺失或重复。");
            foreach (var pin in wires.SelectMany(Children).Where(e => e.LocalName == "NameCon" || e.LocalName == "IdentCon"))
                if (!ids.Contains(Attr(pin,"UId"))) throw new ArgumentException("网络含悬空连线引用。");
            var contact = One(parts.Where(e => Attr(e,"UId") == edit.contact_uid), "触点");
            if (contact.LocalName != "Part" || Attr(contact,"Name") != "Contact") throw new ArgumentException("只支持普通 Contact；沿触点和特殊指令不能通过此入口修改。");
            OnlyAttributes(contact,"Name","UId");
            var contactPins=wires.SelectMany(Children).Where(e=>e.LocalName=="NameCon"&&Attr(e,"UId")==edit.contact_uid).ToArray();
            if(contactPins.Length!=3||new[]{"in","out","operand"}.Any(pin=>contactPins.Count(e=>Attr(e,"Name")==pin)!=1))throw new ArgumentException("触点引脚开路、重复或不受支持。");
            foreach(var pin in contactPins)OnlyAttributes(pin,"UId","Name");
            if (Children(contact).Any(e => e.LocalName != "Negated" || Attr(e,"Name") != "operand") || Children(contact).Length > 1) throw new ArgumentException("触点包含未支持的语义。");
            foreach (var n in Children(contact)) { OnlyAttributes(n,"Name"); if (n.NamespaceURI!=net.NamespaceURI || Children(n).Length != 0) throw new ArgumentException("触点极性节点不完整。"); }
            var operandWire = One(wires.Where(w => Children(w).Any(e => e.LocalName == "NameCon" && Attr(e,"UId") == edit.contact_uid && Attr(e,"Name") == "operand")), "触点操作数连线");
            var ends = Children(operandWire);
            OnlyAttributes(operandWire,"UId");
            if (ends.Length != 2) throw new ArgumentException("共享操作数连线暂不支持。");
            var ident = One(ends.Where(e => e.LocalName == "IdentCon"), "操作数引用");
            OnlyAttributes(ident,"UId");
            if(ends.Any(e=>Children(e).Length!=0||e.NamespaceURI!=net.NamespaceURI))throw new ArgumentException("操作数连线有未支持的结构。");
            var access = One(parts.Where(e => Attr(e,"UId") == Attr(ident,"UId")), "Access");
            if (access.LocalName != "Access" || !new[] { "GlobalVariable", "LocalVariable" }.Contains(Attr(access,"Scope"))) throw new ArgumentException("只支持直接全局或局部符号操作数。");
            OnlyAttributes(access,"Scope","UId");
            if (wires.SelectMany(Children).Count(e => e.LocalName == "IdentCon" && Attr(e,"UId") == Attr(access,"UId")) != 1) throw new ArgumentException("操作数被其他引脚共享，拒绝连带修改。");
            var symbol = One(Children(access), "Symbol");
            if (symbol.LocalName != "Symbol" || Children(symbol).Length == 0) throw new ArgumentException("缺少直接符号路径。");
            OnlyAttributes(symbol);
            if(symbol.NamespaceURI!=net.NamespaceURI||access.NamespaceURI!=net.NamespaceURI||contact.NamespaceURI!=net.NamespaceURI)throw new ArgumentException("触点或操作数命名空间不一致。");
            foreach (var c in Children(symbol)) {
                if (c.NamespaceURI!=net.NamespaceURI || c.LocalName != "Component" || Children(c).Length != 0 || string.IsNullOrWhiteSpace(Attr(c,"Name"))) throw new ArgumentException("数组、切片、间接寻址不支持受限修改。");
                OnlyAttributes(c,"Name");
            }
            if (edit.components != null) {
                if (!new[] { "GlobalVariable", "LocalVariable" }.Contains(edit.scope) || edit.components.Length < 1 || edit.components.Length > 16 || edit.components.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256 || s.Any(char.IsControl) || s.IndexOfAny(new[]{'[',']','"','#','%'}) >= 0)) throw new ArgumentException("components 必须为 1..16 个直接符号成员名，scope 必须明确。");
                symbol.RemoveAll();
                foreach (string s in edit.components) { var c = doc.CreateElement("Component",symbol.NamespaceURI); c.SetAttribute("Name",s); symbol.AppendChild(c); }
                access.SetAttribute("Scope",edit.scope);
            }
            if (edit.negated.HasValue) {
                foreach (var c in Children(contact)) contact.RemoveChild(c);
                if (edit.negated.Value) { var n = doc.CreateElement("Negated",contact.NamespaceURI); n.SetAttribute("Name","operand"); contact.AppendChild(n); }
            }
            if (language == "GRAPH") GraphEditor.ValidateDocument(doc);
            BlockOperations.ValidateCompileUnitOrder(doc);
            return doc.OuterXml;
        }
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32*1024*1024 }; }
        static string DirectoryFor(string root) { string d = Path.Combine(root,"logs","logic-plans"); Directory.CreateDirectory(d); if ((File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0) throw new IOException("方案目录不能为链接。"); return d; }
        static string Id(string id) { Guid value; if (!Guid.TryParse(id,out value) || value == Guid.Empty) throw new ArgumentException("方案编号无效。"); return value.ToString("N"); }
        internal static LogicPlan Create(string root,string project,string plc,string name,string group,string before,ContactEdit edit)
        {
            string after = Patch(before,edit);
            var doc = BlockReadable.Parse(before);
            if (doc.SelectSingleNode("/Document/*/AttributeList/Name").InnerText != name) throw new InvalidOperationException("实时块身份不一致。");
            if (XmlBatch.RecoverySignature(before) == XmlBatch.RecoverySignature(after)) throw new ArgumentException("修改后与原逻辑相同，无需写入。");
            var plan = new LogicPlan { id=Guid.NewGuid().ToString("N"), project_path=Path.GetFullPath(project), plc_name=plc, block_name=name,group_path=group,created_utc=DateTime.UtcNow.ToString("o"),before_xml=before,after_xml=after,before_signature=XmlBatch.RecoverySignature(before),after_signature=XmlBatch.RecoverySignature(after),edit=edit };
            using (var file = new FileStream(Path.Combine(DirectoryFor(root),plan.id+".json"),FileMode.CreateNew,FileAccess.Write))
            using (var writer = new StreamWriter(file,new UTF8Encoding(false))) writer.Write(Json().Serialize(plan));
            return plan;
        }
        internal static LogicPlan Load(string root,string id,string project,string plc)
        {
            string file = Path.Combine(DirectoryFor(root),Id(id)+".json");
            if ((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 || new FileInfo(file).Length>24*1024*1024) throw new IOException("方案文件不安全或超限。");
            var p = Json().Deserialize<LogicPlan>(File.ReadAllText(file));
            if (p.schema!=1 || p.id!=Id(id) || !string.Equals(p.project_path,Path.GetFullPath(project),StringComparison.OrdinalIgnoreCase) || p.plc_name!=plc) throw new InvalidOperationException("方案工程或 PLC 与当前目标不一致。");
            if (XmlBatch.RecoverySignature(p.before_xml)!=p.before_signature || XmlBatch.RecoverySignature(p.after_xml)!=p.after_signature || Patch(p.before_xml,p.edit)!=p.after_xml) throw new InvalidOperationException("方案指纹或受限修改内容不一致。");
            return p;
        }
        internal static object Apply(string root, LogicPlan p, Func<string> read, Func<string,string,XmlBatchResult> import)
        {
            string dir=DirectoryFor(root), receipt=Path.Combine(dir,p.id+".result.json"), claim=Path.Combine(dir,p.id+".claim");
            if (File.Exists(receipt)) return Json().DeserializeObject(File.ReadAllText(receipt));
            if (File.Exists(claim)) throw new InvalidOperationException("该方案曾开始写入但结果未知；先核对备份与工程，禁止自动重放。");
            if (XmlBatch.RecoverySignature(read())!=p.before_signature) throw new InvalidOperationException("实时源码已变化；尚未写入，请重新预览。");
            using (var f=new FileStream(claim,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) { }
            var result=new Dictionary<string,object>{{"plan_id",p.id},{"project_path",p.project_path},{"plc_name",p.plc_name},{"block_name",p.block_name},{"saved",false},{"downloaded",false},{"ok",false},{"state","needs_review"}};
            try {
                string items=Json().Serialize(new[]{new { xml=p.after_xml,group_path=p.group_path,expected_recovery_signature=p.before_signature }});
                var batch=import(items,p.id); result["batch"]=batch;
                string actual=XmlBatch.RecoverySignature(read()); result["readback_signature"]=actual;
                bool ok=batch.ok&&batch.compiled&&batch.error_count==0&&actual==p.after_signature;
                result["ok"]=ok;result["state"]=ok?"verified_unsaved":"needs_review";
            } catch(Exception ex) { result["error"]=ex.Message; }
            result["completed_utc"]=DateTime.UtcNow.ToString("o");
            string temp=receipt+".tmp";File.WriteAllText(temp,Json().Serialize(result),new UTF8Encoding(false));File.Move(temp,receipt);
            return result;
        }
    }
}
