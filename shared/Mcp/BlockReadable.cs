using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Mcp
{
    // A port-level structural view, not a claim of equivalent executable pseudo-code.
    internal static class BlockReadable
    {
        internal static XmlDocument Parse(string xml)
        {
            if(xml==null || xml.Length>8*1024*1024)throw new ArgumentException("源码为空或超过 8 MiB。");
            var doc=new XmlDocument { XmlResolver=null, PreserveWhitespace=true };
            using(var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024 }))doc.Load(reader);
            var pending=new Stack<Tuple<XmlElement,int>>();pending.Push(Tuple.Create(doc.DocumentElement,0));int count=0;
            while(pending.Count>0){var item=pending.Pop();if(++count>100000||item.Item2>80)throw new InvalidOperationException("XML 节点数量或深度超限。");foreach(var c in Children(item.Item1))pending.Push(Tuple.Create(c,item.Item2+1));}
            return doc;
        }
        internal static XmlElement[] Children(XmlNode n){return n.ChildNodes.OfType<XmlElement>().ToArray();}
        static Dictionary<string,object> Map(params object[] fields){var d=new Dictionary<string,object>();for(int i=0;i<fields.Length;i+=2)d.Add((string)fields[i],fields[i+1]);return d;}
        static object Attributes(XmlElement n){return n.Attributes.Cast<XmlAttribute>().ToDictionary(a=>a.Name,a=>(object)a.Value);}
        static string Attr(XmlElement n,string a){return n.GetAttribute(a);}
        static string Title(XmlElement unit,string kind){return string.Join(" / ",unit.SelectNodes("./ObjectList/MultilingualText[@CompositionName='"+kind+"']//Text").Cast<XmlNode>().Select(x=>x.InnerText));}
        static string Anchor(XmlElement n){return n.LocalName+"#"+(n.HasAttribute("UId")?Attr(n,"UId"):n.HasAttribute("ID")?Attr(n,"ID"):Attr(n,"Number"));}
        static object Opaque(XmlElement n,StringBuilder text,List<string> warnings,string path)
        {
            string marker="[OPAQUE_NODE: "+Anchor(n)+"]";warnings.Add(path+": "+marker);
            text.AppendLine(path+" "+marker+" 原文="+(n.OuterXml.Length<=700?n.OuterXml:n.OuterXml.Substring(0,700)+" …[片段，完整 raw_xml 在结构化结果]"));
            return Map("anchor",path,"kind",n.LocalName,"opaque",true,"attributes",Attributes(n),"raw_xml",n.OuterXml);
        }
        static object Flg(XmlElement net,StringBuilder text,List<string> warnings,string path)
        {
            var parts=net.SelectSingleNode("*[local-name()='Parts']");var wires=net.SelectSingleNode("*[local-name()='Wires']");
            if(parts==null||wires==null)return Opaque(net,text,warnings,path);
            var nodes=Children(parts);var ids=nodes.Where(x=>x.HasAttribute("UId")).GroupBy(x=>Attr(x,"UId")).ToDictionary(g=>g.Key,g=>g.ToArray());
            var wireIds=Children(wires).GroupBy(w=>Attr(w,"UId")).ToDictionary(g=>g.Key,g=>g.Count());
            var rendered=new List<object>();var links=new List<object>();
            foreach(var node in nodes) {
                string id=Attr(node,"UId"),label=node.LocalName;bool known=true;
                if(id.Length==0 || ids[id].Length!=1)known=false;
                if(node.LocalName=="Access") {
                    var components=node.SelectNodes(".//*[local-name()='Component']").Cast<XmlElement>().ToArray();
                    var constant=node.SelectSingleNode(".//*[local-name()='ConstantValue']");
                    label=Attr(node,"Scope")+":"+(components.Length>0?string.Join(".",components.Select(x=>"\""+Attr(x,"Name")+"\"")):constant==null?node.InnerText:constant.InnerText);
                    var address=node.SelectSingleNode(".//*[local-name()='Address']") as XmlElement;
                    if(address!=null)label+=" "+string.Join(" ",address.Attributes.Cast<XmlAttribute>().Select(a=>a.Name+"="+a.Value));
                    var allowed=new[]{"Symbol","Component","Constant","ConstantType","ConstantValue","Address"};
                    if(node.SelectNodes(".//*").Cast<XmlElement>().Any(x=>!allowed.Contains(x.LocalName)))known=false;
                } else if(node.LocalName=="Part") {
                    string type=Attr(node,"Name");label=type;
                    if(!new[]{"Contact","PContact","NContact","Coil","SCoil","RCoil","O","A"}.Contains(type))known=false;
                    var neg=Children(node).Where(x=>x.LocalName=="Negated").Select(x=>Attr(x,"Name")).ToArray();
                    if(type=="Contact")label=neg.Contains("operand")?"常闭触点 (NOT operand)":"常开触点";
                    if(type=="PContact")label="上升沿触点（含 bit 存储端口）";
                    if(type=="NContact")label="下降沿触点（含 bit 存储端口）";
                    if(type=="Coil")label="普通线圈";if(type=="SCoil")label="置位 S 线圈";if(type=="RCoil")label="复位 R 线圈";
                    if(neg.Length>0)label+=" Negated="+string.Join(",",neg);
                    if(Children(node).Any(x=>x.LocalName!="Negated"&&x.LocalName!="TemplateValue"))known=false;
                } else if(node.LocalName=="Call") {
                    var info=node.SelectSingleNode("*[local-name()='CallInfo']") as XmlElement;
                    label=info==null?"未知调用":"调用 "+Attr(info,"BlockType")+" "+Attr(info,"Name");
                    if(info==null)known=false;
                } else known=false;
                if(!known){rendered.Add(Opaque(node,text,warnings,path+"/"+Anchor(node)));continue;}
                // Retain all instruction options/call interface/instance metadata verbatim as a tree.
                var details=Children(node).Select(PlainTree).ToArray();
                rendered.Add(Map("anchor",path+"/"+Anchor(node),"uid",id,"kind",node.LocalName,"label",label,"attributes",Attributes(node),"details",details));
                text.AppendLine("  "+Anchor(node)+" "+label);
                if(node.LocalName=="Call")foreach(var p in node.SelectNodes(".//*[local-name()='Parameter' or local-name()='Instance']").Cast<XmlElement>())text.AppendLine("    "+p.LocalName+" "+string.Join(" ",p.Attributes.Cast<XmlAttribute>().Select(a=>a.Name+"="+a.Value))+" "+p.InnerText+" "+string.Join(".",p.SelectNodes(".//*[local-name()='Component']").Cast<XmlElement>().Select(c=>Attr(c,"Name"))));
            }
            foreach(var wire in Children(wires)) {
                var endpoints=new List<object>();var labels=new List<string>();bool valid=wire.LocalName=="Wire"&&Attr(wire,"UId").Length>0
                    && !ids.ContainsKey(Attr(wire,"UId")) && wireIds[Attr(wire,"UId")]==1;
                foreach(var pin in Children(wire)) {
                    string id=Attr(pin,"UId");
                    if(!new[]{"NameCon","IdentCon","Powerrail","OpenCon"}.Contains(pin.LocalName))valid=false;
                    if((pin.LocalName=="NameCon"||pin.LocalName=="IdentCon")&&(!ids.ContainsKey(id)||ids[id].Length!=1))valid=false;
                    endpoints.Add(PlainTree(pin));labels.Add(pin.LocalName+(id.Length==0?"":"#"+id)+(pin.HasAttribute("Name")?"."+Attr(pin,"Name"):""));
                }
                if(!valid){links.Add(Opaque(wire,text,warnings,path+"/"+Anchor(wire)));continue;}
                links.Add(Map("anchor",path+"/"+Anchor(wire),"attributes",Attributes(wire),"endpoints",endpoints.ToArray()));
                text.AppendLine("  "+Anchor(wire)+": "+string.Join(" <-> ",labels));
            }
            var extras=Children(net).Where(x=>x!=parts&&x!=wires).Select(x=>Opaque(x,text,warnings,path+"/"+Anchor(x))).ToArray();
            var conditions=BooleanConditions(net,text,warnings,path);
            return Map("kind","FlgNet","anchor",path,"namespace",net.NamespaceURI,"attributes",Attributes(net),"nodes",rendered.ToArray(),"wires",links.ToArray(),"extra",extras,"boolean_conditions",conditions);
        }
        // Intentionally small subset. All port topology remains available even when this refuses a network.
        static object[] BooleanConditions(XmlElement net,StringBuilder text,List<string> warnings,string path)
        {
            try {
                if(!new[]{"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4","http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"}.Contains(net.NamespaceURI))throw new InvalidOperationException("未验证的 FlgNet 版本，仅展示结构");
                if(Children(net).Any(c=>c.LocalName!="Parts"&&c.LocalName!="Wires"))throw new InvalidOperationException("网络包含额外结构");
                var parts=Children(net.SelectSingleNode("*[local-name()='Parts']"));var wires=Children(net.SelectSingleNode("*[local-name()='Wires']"));
                if(parts.Any(n=>n.LocalName!="Access"&&n.LocalName!="Part"))throw new InvalidOperationException("调用或其他节点需要查看端口结构");
                var byId=parts.ToDictionary(n=>Attr(n,"UId"));
                if(wires.GroupBy(w=>Attr(w,"UId")).Any(g=>g.Key.Length==0||g.Count()!=1)||wires.Any(w=>byId.ContainsKey(Attr(w,"UId"))))throw new InvalidOperationException("连线标识不唯一");
                if(parts.Where(p=>p.LocalName=="Part").Any(p=>p.Attributes.Cast<XmlAttribute>().Any(a=>a.Name!="UId"&&a.Name!="Name")))throw new InvalidOperationException("指令含额外属性，仅展示结构");
                if(byId.ContainsKey("")||parts.Where(n=>n.LocalName=="Part").Any(n=>!new[]{"Contact","Coil","SCoil","RCoil","O"}.Contains(Attr(n,"Name"))))throw new InvalidOperationException("指令不在常规触点/线圈/或分支子集");
                foreach(var wire in wires)foreach(var pin in Children(wire)) {
                    if(wire.LocalName!="Wire"||wire.Attributes.Cast<XmlAttribute>().Any(a=>a.Name!="UId")||Children(pin).Length>0
                        ||pin.Attributes.Cast<XmlAttribute>().Any(a=>pin.LocalName=="Powerrail"||a.Name!="UId"&&(pin.LocalName!="NameCon"||a.Name!="Name")))throw new InvalidOperationException("连线含额外语义");
                    if(!new[]{"NameCon","IdentCon","Powerrail"}.Contains(pin.LocalName))throw new InvalidOperationException("包含开路或特殊连线");
                    if(pin.LocalName!="Powerrail"&&!byId.ContainsKey(Attr(pin,"UId")))throw new InvalidOperationException("连线引用不完整");
                    if(pin.LocalName=="IdentCon"&&byId[Attr(pin,"UId")].LocalName!="Access")throw new InvalidOperationException("操作数引用类型不匹配");
                    if(pin.LocalName=="NameCon") {
                        var target=byId[Attr(pin,"UId")];string port=Attr(pin,"Name");
                        bool allowed=Attr(target,"Name")=="O"?(port=="out"||System.Text.RegularExpressions.Regex.IsMatch(port,"^in[0-9]+$")):new[]{"in","out","operand"}.Contains(port);
                        if(target.LocalName!="Part"||!allowed)throw new InvalidOperationException("存在未支持的指令端口");
                    }
                }
                Func<string,string,XmlElement[]> endpoints=(id,pin)=>{
                    var hits=wires.Where(w=>Children(w).Any(c=>c.LocalName=="NameCon"&&Attr(c,"UId")==id&&Attr(c,"Name")==pin)).ToArray();
                    if(hits.Length!=1)throw new InvalidOperationException("端口开路或重复连接");return Children(hits[0]);
                };
                Func<string,string> operand=id=>{
                    var ends=endpoints(id,"operand");if(ends.Length!=2)throw new InvalidOperationException("操作数不是一对一直连");
                    var refs=ends.Where(x=>x.LocalName=="IdentCon").ToArray();if(refs.Length!=1)throw new InvalidOperationException("操作数没有唯一符号");
                    var access=byId[Attr(refs[0],"UId")];var symbol=Children(access).SingleOrDefault();
                    if(access.LocalName!="Access"||!new[]{"GlobalVariable","LocalVariable"}.Contains(Attr(access,"Scope"))||symbol==null||symbol.LocalName!="Symbol")throw new InvalidOperationException("仅支持直接符号操作数");
                    var names=Children(symbol);if(names.Length==0||names.Any(c=>c.LocalName!="Component"||Children(c).Length>0||c.Attributes.Cast<XmlAttribute>().Any(a=>a.Name!="Name"))
                        ||access.Attributes.Cast<XmlAttribute>().Any(a=>a.Name!="UId"&&a.Name!="Scope")||symbol.Attributes.Count>0)throw new InvalidOperationException("复合/动态操作数未转换");
                    return (Attr(access,"Scope")=="LocalVariable"?"#":"")+string.Join(".",names.Select(c=>"\""+Attr(c,"Name")+"\""));
                };
                var active=new HashSet<string>();var visited=new HashSet<string>();int visits=0;
                Func<string,Dictionary<string,object>> output=null;Func<string,string,Dictionary<string,object>> input=null;
                input=(id,pin)=>{
                    var ends=endpoints(id,pin);var drivers=ends.Where(x=>x.LocalName=="Powerrail"||x.LocalName=="NameCon"&&Attr(x,"Name")=="out").ToArray();
                    if(drivers.Length!=1)throw new InvalidOperationException("无法唯一确定信号源；未猜测并联语义");
                    foreach(var end in ends)if(end.LocalName!="Powerrail"&&(end.LocalName!="NameCon"||!(Attr(end,"Name")=="out"||Attr(end,"Name")=="in"||System.Text.RegularExpressions.Regex.IsMatch(Attr(end,"Name"),"^in[0-9]+$"))))throw new InvalidOperationException("特殊信号端口");
                    return drivers[0].LocalName=="Powerrail"?Map("kind","powerrail","expression","TRUE"):output(Attr(drivers[0],"UId"));
                };
                output=id=>{
                    if(++visits>2000||active.Count>64||!active.Add(id))throw new InvalidOperationException("环路或表达式规模超限");
                    try {
                        var part=byId[id];visited.Add(id);string type=Attr(part,"Name");
                        if(type=="Contact") {
                            if(Children(part).Length>1||Children(part).Any(c=>c.LocalName!="Negated"||Attr(c,"Name")!="operand"||c.Attributes.Count!=1||Children(c).Length>0))throw new InvalidOperationException("触点含额外语义");
                            var upstream=input(id,"in");bool neg=Children(part).Any(c=>c.LocalName=="Negated");string symbol=operand(id);
                            var test=Map("kind",neg?"not":"symbol","symbol",symbol,"expression",neg?"NOT "+symbol:symbol,"uid",id);
                            return Map("kind","and","uid",id,"children",new[]{upstream,test},"expression","("+upstream["expression"]+" AND "+test["expression"]+")");
                        }
                        if(type=="O") {
                            var children=Children(part);int card;
                            if(children.Length!=1||children[0].LocalName!="TemplateValue"||Attr(children[0],"Name")!="Card"||Attr(children[0],"Type")!="Cardinality"||children[0].Attributes.Count!=2||!int.TryParse(children[0].InnerText,out card)||card<2||card>64)throw new InvalidOperationException("或分支基数未知");
                            var branches=Enumerable.Range(1,card).Select(i=>input(id,"in"+i)).ToArray();
                            return Map("kind","or","uid",id,"children",branches,"expression","("+string.Join(" OR ",branches.Select(b=>(string)b["expression"]))+")");
                        }
                        throw new InvalidOperationException("信号链包含非触点节点");
                    }finally{active.Remove(id);}
                };
                var conditions=new List<object>();
                foreach(var coil in parts.Where(n=>n.LocalName=="Part"&&new[]{"Coil","SCoil","RCoil"}.Contains(Attr(n,"Name")))) {
                    if(Children(coil).Length>0)throw new InvalidOperationException("线圈含反相或额外语义");
                    string id=Attr(coil,"UId"),target=operand(id);visited.Add(id);var tree=input(id,"in");
                    conditions.Add(Map("anchor",path+"/"+Anchor(coil),"coil_type",Attr(coil,"Name"),"target",target,"condition_tree",tree));
                }
                if(parts.Where(n=>n.LocalName=="Part").Any(n=>!visited.Contains(Attr(n,"UId"))))throw new InvalidOperationException("存在未纳入条件树的指令；保留完整接线视图");
                foreach(Dictionary<string,object> condition in conditions)text.AppendLine("  条件树 "+condition["anchor"]+": "+((Dictionary<string,object>)condition["condition_tree"])["expression"]+" => "+condition["coil_type"]+" "+condition["target"]);
                return conditions.ToArray();
            }catch(Exception ex){warnings.Add(path+": 未生成布尔简图："+ex.Message);return new object[0];}
        }
        static object PlainTree(XmlElement n){return Map("kind",n.LocalName,"namespace",n.NamespaceURI,"attributes",Attributes(n),"text",Children(n).Length==0?n.InnerText:"","children",Children(n).Select(PlainTree).ToArray());}
        static object Graph(XmlElement n,StringBuilder text,List<string> warnings,string path)
        {
            if(n.LocalName=="FlgNet")return Flg(n,text,warnings,path);
            // Preserve GRAPH topology and conditions; action semantics are intentionally opaque.
            var allowed=new[]{"Graph","Sequences","Sequence","Steps","Step","Transitions","Transition","Branches","Branch","Connections","Connection","NodeFrom","NodeTo","StepRef","TransitionRef","BranchRef","EndConnection","LinkType","Condition","NetworkSource"};
            if(!allowed.Contains(n.LocalName))return Opaque(n,text,warnings,path);
            text.AppendLine(path+" "+string.Join(" ",n.Attributes.Cast<XmlAttribute>().Select(a=>a.Name+"="+a.Value))+(Children(n).Length==0?" "+n.InnerText:""));
            return Map("kind",n.LocalName,"anchor",path,"attributes",Attributes(n),"text",Children(n).Length==0?n.InnerText:"","children",Children(n).Select((c,i)=>Graph(c,text,warnings,path+"/"+Anchor(c)+"["+i+"]")).ToArray());
        }
        internal static Dictionary<string,object> Read(string xml,int offset,int limit)
        {
            if(offset<0||limit<1||limit>20)throw new ArgumentException("network_offset >= 0，network_limit 为 1..20。");
            var doc=Parse(xml);var units=doc.SelectNodes("//*[local-name()='SW.Blocks.CompileUnit']").Cast<XmlElement>().ToArray();
            var text=new StringBuilder("结构化接线视图：端口连线保留原方向属性；<-> 表示同一连线，不推断执行顺序。不是等价伪代码。\n");
            var warnings=new List<string>();var output=new List<object>();
            foreach(var pair in units.Select((u,i)=>new{unit=u,index=i}).Skip(offset).Take(limit)) {
                var unit=pair.unit;string anchor="NW"+(pair.index+1)+"/CompileUnit#"+Attr(unit,"ID");
                string title=Title(unit,"Title"),comment=Title(unit,"Comment");text.AppendLine(anchor+" "+title);if(comment.Length>0)text.AppendLine(comment);
                var source=unit.SelectSingleNode("./AttributeList/NetworkSource");var contents=new List<object>();
                if(source==null){contents.Add(Opaque(unit,text,warnings,anchor));}
                else foreach(var node in Children(source)) {
                    if(node.LocalName=="FlgNet")contents.Add(Flg(node,text,warnings,anchor));
                    else if(node.LocalName=="Graph")contents.Add(Graph(node,text,warnings,anchor+"/Graph"));
                    else contents.Add(Opaque(node,text,warnings,anchor+"/"+Anchor(node)));
                }
                output.Add(Map("network",pair.index+1,"compile_unit_id",Attr(unit,"ID"),"title",title,"comment",comment,"logic",contents.ToArray()));
            }
            if(units.Length==0){warnings.Add("未发现 CompileUnit；此对象不在 LAD/GRAPH 网络视图覆盖范围。");text.AppendLine(warnings[0]);}
            bool paged=offset>0||offset+output.Count<units.Length;
            if(paged)text.AppendLine("[网络分页] 当前 "+offset+" 起，共返回 "+output.Count+" / "+units.Length+" 个网络。");
            return Map("schema",1,"source_hash",IndexJson.Hash(xml),"view_kind","port_topology","semantic_equivalence_proven",false,
                "scope","NetworkSource only; interface and other block metadata remain in original XML", "total_networks",units.Length,"network_offset",offset,
                "next_network_offset",offset+output.Count<units.Length?(object)(offset+output.Count):null,"page_complete",!paged,
                "coverage",warnings.Count>0?"partial":"structural_only","warnings",warnings.ToArray(),"networks",output.ToArray(),"readable_text",text.ToString());
        }
    }
}
