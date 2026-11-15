using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    // A field patcher, never a replacement XML supplied by the browser. Everything
    // outside the explicitly supported fields remains in the original document.
    internal sealed class HmiPageEditor
    {
        internal sealed class Field
        {
            public string key, label, kind, value, control;
            internal XmlNode node;
        }
        internal readonly XmlDocument Document;
        internal readonly Dictionary<string, Field> Fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        internal readonly List<Dictionary<string, object>> Controls = new List<Dictionary<string, object>>();
        internal readonly List<string> Reasons = new List<string>();
        internal string Name, Kind;
        internal int Width, Height;
        private static readonly string[] Supported = {"Button","TextField","IOField","SymbolicIOField","Circle","Ellipse","Rectangle","Line","GraphicIOField","DateTimeField"};
        internal static string V(XmlNode node, string path) { return HmiValidation.Value(node,path); }
        private static int N(XmlNode node, string name, int fallback)
        { int n; return int.TryParse(V(node,"AttributeList/"+name),out n)?n:fallback; }
        private static string Identity(XmlNode node)
        {
            if(node==null||node is XmlDocument)return "";
            string name=V(node,"AttributeList/ObjectName");if(name.Length==0)name=V(node,"AttributeList/Name");
            if(name.Length==0)name=V(node,"AttributeList/Culture");
            if(node.Attributes!=null&&node.Attributes["CompositionName"]!=null)name+="|"+node.Attributes["CompositionName"].Value;
            if(name.Length==0&&node.ParentNode!=null)name=node.ParentNode.ChildNodes.Cast<XmlNode>().TakeWhile(n=>n!=node).Count(n=>n.Name==node.Name).ToString();
            return Identity(node.ParentNode)+"/"+node.Name+"["+name.Length+":"+name+"]";
        }
        private static XmlNode Owner(XmlNode node)
        {
            for(var p=node;p!=null;p=p.ParentNode)if(p.Attributes!=null&&p.Attributes["CompositionName"]!=null&&p.Attributes["CompositionName"].Value=="ScreenItems")return p;
            return null;
        }
        internal static bool ProtectedName(string name)
        { return Regex.IsMatch(name??"",@"alarm|diagnos|报警|诊断",RegexOptions.IgnoreCase); }
        private Field Add(XmlNode control, XmlNode node, string kind, string label)
        {
            var field=new Field{key=Identity(node),control=Identity(control),node=node,value=kind=="text"?PlainText(node):node.InnerText,label=label,kind=kind};
            if(Fields.ContainsKey(field.key))throw new ArgumentException("页面字段标识重复，暂不能编辑。");Fields.Add(field.key,field);return field;
        }
        private static string PlainText(XmlNode node)
        {
            if(node.Name=="br")return "\n";
            if(node.NodeType==XmlNodeType.Text||node.NodeType==XmlNodeType.CDATA)return node.Value;
            return string.Join(node.Name=="body"?"\n":"",node.ChildNodes.Cast<XmlNode>().Select(PlainText));
        }
        internal HmiPageEditor(string xml)
        {
            Document=HmiValidation.Parse(xml);
            var roots=Document.SelectNodes("/Document/Hmi.Screen.Screen | /Document/Hmi.Screen.ScreenTemplate");
            if(roots.Count!=1)throw new ArgumentException("只支持经典 HMI 普通画面与模板。");
            var root=roots[0];Name=V(root,"AttributeList/Name");Kind=root.Name.EndsWith("ScreenTemplate")?"screen_templates":"screens";
            Width=N(root,"Width",800);Height=N(root,"Height",480);
            if(Width<1||Height<1||Width>4096||Height>4096)throw new ArgumentException("页面尺寸超出支持范围。");
            if(ProtectedName(Name))Reasons.Add("报警或诊断页面保持只读，以保留不能完整导出的复合控件。");
            foreach(XmlNode control in Document.SelectNodes("//*[@CompositionName='ScreenItems']"))
            {
                string type=control.Name.Substring(control.Name.LastIndexOf('.')+1),name=V(control,"AttributeList/ObjectName");
                bool supported=Supported.Contains(type);var fields=new List<Field>();
                if(!supported&&type!="Group")Reasons.Add("包含尚未支持的控件："+name+"（"+type+"）。");
                if(supported)
                {
                    foreach(string attribute in new[]{"Left","Top","Width","Height","BackColor","ForeColor","BorderWidth"})
                    {var node=control.SelectSingleNode("AttributeList/"+attribute);if(node!=null)fields.Add(Add(control,node,attribute.EndsWith("Color")?"color":"number",attribute));}
                    if(type=="IOField"||type=="SymbolicIOField")
                    {var mode=control.SelectSingleNode("AttributeList/Mode");if(mode!=null)fields.Add(Add(control,mode,"mode","输入输出方式"));}
                    foreach(XmlNode text in control.SelectNodes(".//MultilingualTextItem/AttributeList/Text"))
                    {
                        if(Owner(text)!=control)continue;
                        var ml=text.ParentNode.ParentNode.ParentNode.ParentNode;
                        string composition=ml.Attributes==null||ml.Attributes["CompositionName"]==null?"":ml.Attributes["CompositionName"].Value;
                        if(!new[]{"Text","TextOff","TextOn"}.Contains(composition))continue;
                        // Complex rich text is preserved and displayed, but not flattened on write.
                        if(text.SelectNodes(".//*").Cast<XmlNode>().Any(n=>!new[]{"body","p","br"}.Contains(n.Name)))continue;
                        fields.Add(Add(control,text,"text",composition+" · "+V(text.ParentNode,"Culture")));
                    }
                    foreach(XmlNode link in control.SelectNodes(".//LinkList/Tag/Name | .//LinkList/TextList/Name"))
                        if(Owner(link)==control)fields.Add(Add(control,link,link.ParentNode.Name=="Tag"?"tag":"text_list",link.ParentNode.Name=="Tag"?"变量 · "+link.ParentNode.ParentNode.ParentNode.Name.Split('.').Last():"文本列表"));
                    foreach(XmlNode link in control.SelectNodes(".//Hmi.Event.FunctionListEntry[AttributeList/Name='ActivateScreen']/ObjectList/Hmi.Event.FunctionListEntryParameter[AttributeList/Name='Screen name']/LinkList/Value/Name"))
                        if(Owner(link)==control)fields.Add(Add(control,link,"screen","按键页面跳转"));
                }
                string caption=string.Join(" ",control.SelectNodes("./ObjectList/MultilingualText/ ObjectList/MultilingualTextItem/AttributeList/Text".Replace("/ ","/")).Cast<XmlNode>().Select(n=>n.InnerText).Where(s=>s.Length>0).Distinct().Take(2));
                Controls.Add(new Dictionary<string,object>{{"key",Identity(control)},{"name",name},{"type",type},{"reverse_x",N(control,"StartLeft",0)>N(control,"EndLeft",0)},{"reverse_y",N(control,"StartTop",0)>N(control,"EndTop",0)},{"left",N(control,"Left",0)},{"top",N(control,"Top",0)},{"width",N(control,"Width",40)},{"height",N(control,"Height",24)},{"back_color",V(control,"AttributeList/BackColor")},{"fore_color",V(control,"AttributeList/ForeColor")},{"text",caption},{"editable",supported},{"fields",fields.Select(f=>new{f.key,f.label,f.kind,f.value}).ToArray()}});
            }
        }
        internal object Model()
        {return new{name=Name,kind=Kind,width=Width,height=Height,controls=Controls,read_only=Reasons.Count>0,reasons=Reasons.Distinct().ToArray(),note="布局示意，不模拟 PLC 数值、字体、图片、模板叠加或运行时动画。经典接口可能遗漏复合控件；写入普通页面前需确认不含这类控件。"};}
        internal object[] Patch(string changesJson, IDictionary<string,string> tags, ISet<string> screens, ISet<string> textLists)
        {
            if(Reasons.Count>0)throw new InvalidOperationException(string.Join(" ",Reasons));
            var rows=CylinderWritePipeline.Json().Deserialize<Dictionary<string,string>[]>(changesJson);
            if(rows==null||rows.Length==0||rows.Length>1000)throw new ArgumentException("请选择 1–1000 个字段修改。");
            var seen=new HashSet<string>();var differences=new List<object>();
            foreach(var row in rows)
            {
                string key,value;Field field;
                if(row==null||row.Count!=2||!row.TryGetValue("key",out key)||!row.TryGetValue("value",out value)||key==null||value==null||!seen.Add(key)||!Fields.TryGetValue(key,out field))throw new ArgumentException("字段不存在、重复或不允许编辑。");
                if(field.value==value)continue;
                if(value.Length>2048||value.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t'))throw new ArgumentException("字段文本过长或含无效字符。");
                if(field.kind=="number")
                {int n;if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out n)||n<0||n>4096||(field.label=="Width"||field.label=="Height")&&n<1||field.label=="BorderWidth"&&n>20)throw new ArgumentException("位置、尺寸或边框值超出范围。");value=n.ToString(CultureInfo.InvariantCulture);}
                if(field.kind=="color"&&!Regex.IsMatch(value,@"^\d{1,3},\s*\d{1,3},\s*\d{1,3}$"))throw new ArgumentException("颜色使用 R, G, B。");
                if(field.kind=="color"&&value.Split(',').Any(s=>int.Parse(s.Trim())>255))throw new ArgumentException("颜色分量须在 0–255。");
                if(field.kind=="mode"&&!new[]{"Input","Output","InOutput"}.Contains(value))throw new ArgumentException("输入输出方式无效。");
                if(field.kind=="tag")
                {string beforeType,afterType;if(!tags.TryGetValue(field.value,out beforeType)||!tags.TryGetValue(value,out afterType)||string.IsNullOrEmpty(beforeType)||beforeType!=afterType)throw new ArgumentException("新旧变量须存在且类型已知并相同；不能推测结构体成员类型。");}
                if(field.kind=="screen"&&!screens.Contains(value))throw new ArgumentException("跳转页面不存在。");
                if(field.kind=="text_list"&&!textLists.Contains(value))throw new ArgumentException("文本列表不存在。");
                differences.Add(new{key=field.key,control=Controls.Single(c=>Convert.ToString(c["key"])==field.control)["name"],field=field.label,before=field.value,after=value});
                if(field.kind=="text")
                {field.node.RemoveAll();var body=Document.CreateElement("body");field.node.AppendChild(body);foreach(var line in value.Replace("\r\n","\n").Split('\n')){var p=Document.CreateElement("p");p.InnerText=line;body.AppendChild(p);}}
                else field.node.InnerText=value;
            }
            // Geometry is represented both as a bounding box and as endpoints.
            foreach(XmlNode line in Document.SelectNodes("//Hmi.Screen.Line")) {
                if(!rows.Any(r=>Fields[r["key"]].control==Identity(line)&&Fields[r["key"]].value!=r["value"]&&new[]{"Left","Top","Width","Height"}.Contains(Fields[r["key"]].label)))continue;
                var a=line.SelectSingleNode("AttributeList");
                if(new[]{"StartLeft","StartTop","EndLeft","EndTop"}.Any(k=>a.SelectSingleNode(k)==null))throw new ArgumentException("线段缺少起终点坐标，无法安全调整。");
                bool reverseX=N(line,"StartLeft",0)>N(line,"EndLeft",0),reverseY=N(line,"StartTop",0)>N(line,"EndTop",0);
                int x=N(line,"Left",0),y=N(line,"Top",0),w=N(line,"Width",0),h=N(line,"Height",0);
                a.SelectSingleNode("StartLeft").InnerText=(reverseX?x+w:x).ToString();a.SelectSingleNode("EndLeft").InnerText=(reverseX?x:x+w).ToString();
                a.SelectSingleNode("StartTop").InnerText=(reverseY?y+h:y).ToString();a.SelectSingleNode("EndTop").InnerText=(reverseY?y:y+h).ToString();
            }
            var errors=HmiValidation.Validate(Document.OuterXml);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors));
            return differences.ToArray();
        }
        internal static object[] Verify(string expected,string actual,IEnumerable<string> keys)
        {
            var a=new HmiPageEditor(expected);var b=new HmiPageEditor(actual);var errors=new List<object>();
            var affected=new HashSet<string>();
            foreach(string key in keys){Field wanted,got;if(!a.Fields.TryGetValue(key,out wanted))throw new ArgumentException("核验字段不存在。");if(!b.Fields.TryGetValue(key,out got)||wanted.value!=got.value)errors.Add(new{field=key,expected=wanted.value,actual=got==null?null:got.value});if(new[]{"Left","Top","Width","Height"}.Contains(wanted.label))affected.Add(wanted.control);}
            foreach(XmlNode line in a.Document.SelectNodes("//Hmi.Screen.Line"))if(affected.Contains(Identity(line))) {
                var readback=b.Document.SelectNodes("//Hmi.Screen.Line").Cast<XmlNode>().FirstOrDefault(n=>Identity(n)==Identity(line));
                foreach(string attribute in new[]{"StartLeft","StartTop","EndLeft","EndTop"}) {
                    string wanted=V(line,"AttributeList/"+attribute),got=readback==null?null:V(readback,"AttributeList/"+attribute);
                    if(wanted!=got)errors.Add(new{field=Identity(line)+"/"+attribute,expected=wanted,actual=got});
                }
            }
            foreach(string error in HmiValidation.Validate(actual))errors.Add(new{field="geometry_or_binding",expected="valid",actual=error});
            return errors.ToArray();
        }
    }
}
