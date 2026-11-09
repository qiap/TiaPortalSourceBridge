using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class GraphExpression
    {
        public string op {get;set;} public string symbol {get;set;} public string reason {get;set;}
        public bool? constant {get;set;} public List<GraphExpression> children {get;set;}
    }
    // Deliberately a Boolean contact subset, not a GRAPH interpreter. Unknown
    // instructions invalidate the whole condition instead of hiding operands.
    internal static class GraphMonitor
    {
        static XmlElement Child(XmlNode n,string name){return n.ChildNodes.OfType<XmlElement>().FirstOrDefault(x=>x.LocalName==name);}
        static GraphExpression Node(string op,params GraphExpression[] children){return new GraphExpression{op=op,children=children.ToList()};}
        internal static GraphExpression ParseCondition(XmlElement owner)
        {
            try{
                var nets=owner.SelectNodes(".//*[local-name()='FlgNet']").Cast<XmlElement>().ToArray();
                if(nets.Length!=1||owner.GetAttribute("ProgrammingLanguage")!="LAD")throw new InvalidOperationException("只解析单个 LAD 布尔触点网络。");
                var parts=Child(nets[0],"Parts");var wires=Child(nets[0],"Wires");if(parts==null||wires==null)throw new InvalidOperationException("网络缺少元件或连线。");
                var elements=parts.ChildNodes.OfType<XmlElement>().ToArray();if(elements.Length>512)throw new InvalidOperationException("网络超过 512 个元件。");
                if(elements.Any(x=>x.LocalName!="Access"&&x.LocalName!="Part"))throw new InvalidOperationException("包含未支持的调用或元件。");
                var ids=elements.ToDictionary(x=>x.GetAttribute("UId"),StringComparer.Ordinal);if(ids.ContainsKey(""))throw new InvalidOperationException("网络编号缺失。");
                var logic=elements.Where(x=>x.LocalName=="Part").ToArray();
                if(logic.Any(x=>!new[]{"Contact","TrCoil","IlCoil","SvCoil"}.Contains(x.GetAttribute("Name"))))throw new InvalidOperationException("包含沿、比较、计时或其他未支持指令。");
                foreach(var p in logic)if(p.ChildNodes.OfType<XmlElement>().Any(x=>x.LocalName!="Negated"||x.GetAttribute("Name")!="operand"||p.GetAttribute("Name")!="Contact"))throw new InvalidOperationException("元件修饰不在布尔触点范围。");
                var coils=logic.Where(x=>x.GetAttribute("Name")!="Contact").ToArray();if(coils.Length!=1)throw new InvalidOperationException("需要唯一条件线圈。");
                var links=wires.ChildNodes.OfType<XmlElement>().ToArray();
                if(links.Any(w=>w.LocalName!="Wire"||w.ChildNodes.OfType<XmlElement>().Any(c=>!new[]{"Powerrail","NameCon","IdentCon"}.Contains(c.LocalName))))throw new InvalidOperationException("存在开路或未支持的连线。");
                foreach(var c in links.SelectMany(w=>w.ChildNodes.OfType<XmlElement>()).Where(c=>c.LocalName!="Powerrail")){
                    XmlElement part;if(!ids.TryGetValue(c.GetAttribute("UId"),out part))throw new InvalidOperationException("存在悬空连线。");
                    if(c.LocalName=="NameCon"&&(part.LocalName!="Part"||!new[]{"in","out","operand"}.Contains(c.GetAttribute("Name"))))throw new InvalidOperationException("未识别的引脚。");
                    if(c.LocalName=="IdentCon"&&part.LocalName!="Access")throw new InvalidOperationException("操作数连接无效。");
                }
                Func<string,string,XmlElement[]> connected=(id,pin)=>links.Where(w=>w.ChildNodes.OfType<XmlElement>().Any(c=>c.LocalName=="NameCon"&&c.GetAttribute("UId")==id&&c.GetAttribute("Name")==pin)).ToArray();
                var visited=new HashSet<string>();var stack=new HashSet<string>();int count=0;
                Func<string,GraphExpression> input=null;Func<string,GraphExpression> output=null;
                output=id=>{
                    if(!stack.Add(id))throw new InvalidOperationException("条件含循环连线。");if(++count>2048)throw new InvalidOperationException("条件展开过大。");visited.Add(id);
                    var p=ids[id];if(p.GetAttribute("Name")!="Contact")throw new InvalidOperationException("条件线圈不能作为上游输出。");
                    var ws=connected(id,"operand");if(ws.Length!=1)throw new InvalidOperationException("触点操作数缺失或重复连接。");
                    var operands=ws[0].ChildNodes.OfType<XmlElement>().ToArray();if(operands.Count(x=>x.LocalName=="IdentCon")!=1||operands.Any(x=>x.LocalName!="IdentCon"&&x.LocalName!="NameCon"))throw new InvalidOperationException("操作数连接歧义。");
                    var access=ids[operands.Single(x=>x.LocalName=="IdentCon").GetAttribute("UId")];var symbol=Child(access,"Symbol");
                    if(access.GetAttribute("Scope")!="GlobalVariable"||symbol==null||symbol.ChildNodes.OfType<XmlElement>().Any(x=>x.LocalName!="Component"||x.ChildNodes.OfType<XmlElement>().Any()||x.GetAttribute("Name").Contains("\"")))throw new InvalidOperationException("仅支持明确的全局标量符号；局部、数组、切片与间接寻址未解析。");
                    var names=symbol.ChildNodes.OfType<XmlElement>().Select(x=>x.GetAttribute("Name")).ToArray();if(names.Length==0||names.Any(string.IsNullOrEmpty))throw new InvalidOperationException("符号缺失。");
                    var leaf=new GraphExpression{op="symbol",symbol=string.Join(".",names.Select(x=>"\""+x+"\"")),children=new List<GraphExpression>()};
                    GraphExpression term=Child(p,"Negated")==null?leaf:Node("not",leaf);var result=Node("and",input(id),term);stack.Remove(id);return result;
                };
                input=id=>{
                    var ws=connected(id,"in");if(ws.Length!=1)throw new InvalidOperationException("条件输入缺失或重复连接。");
                    var drivers=new List<GraphExpression>();foreach(var c in ws[0].ChildNodes.OfType<XmlElement>()){
                        if(c.LocalName=="Powerrail")drivers.Add(new GraphExpression{op="constant",constant=true,children=new List<GraphExpression>()});
                        else if(c.LocalName=="NameCon"&&c.GetAttribute("Name")=="out")drivers.Add(output(c.GetAttribute("UId")));
                        else if(c.LocalName!="NameCon"||c.GetAttribute("Name")!="in")throw new InvalidOperationException("电源连线混入操作数。");
                    }if(drivers.Count==0)throw new InvalidOperationException("没有可识别上游电源。");return drivers.Count==1?drivers[0]:Node("or",drivers.ToArray());
                };
                var tree=input(coils[0].GetAttribute("UId"));if(logic.Count(p=>p.GetAttribute("Name")=="Contact")!=visited.Count)throw new InvalidOperationException("有未连接到条件线圈的触点。");return tree;
            }catch(Exception ex){return new GraphExpression{op="unknown",reason=ex.Message,children=new List<GraphExpression>()};}
        }
        internal static object Read(string xml)
        {
            var doc=GraphEditor.Parse(xml);GraphEditor.Inspect(doc,false);
            return doc.SelectNodes("//*[local-name()='Graph']/*[local-name()='Sequence']").Cast<XmlElement>().Select((sequence,index)=>new{
                index,
                steps=sequence.SelectNodes("*[local-name()='Steps']/*[local-name()='Step']").Cast<XmlElement>().Select(s=>new{
                    number=s.GetAttribute("Number"),name=s.GetAttribute("Name"),
                    conditions=s.SelectNodes("*[local-name()='Interlocks' or local-name()='Supervisions']/*").Cast<XmlElement>().Select(c=>new{kind=c.LocalName,tree=ParseCondition(c),xml=c.OuterXml}).ToArray()
                }).ToArray(),
                transitions=sequence.SelectNodes("*[local-name()='Transitions']/*[local-name()='Transition']").Cast<XmlElement>().Select(t=>new{number=t.GetAttribute("Number"),name=t.GetAttribute("Name"),tree=ParseCondition(t),xml=t.OuterXml}).ToArray(),
                connections=sequence.SelectNodes("*[local-name()='Connections']/*").Cast<XmlElement>().Select(c=>new{
                    from=Endpoint(c,"NodeFrom"),to=Endpoint(c,"NodeTo")
                }).ToArray()
            }).ToArray();
        }
        static string Endpoint(XmlElement c,string name){var holder=Child(c,name);var n=holder==null?null:holder.ChildNodes.OfType<XmlElement>().FirstOrDefault();return n==null?"unknown":n.LocalName.Replace("Ref","")+n.GetAttribute("Number");}
    }
}
