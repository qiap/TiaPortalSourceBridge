using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    // A whole alarm DB passed InOut is normally a conflicting writer. The sole
    // exception is a fully inspected, bounded aggregate/reset routine: it may
    // clear array bits under its Boolean reset inputs and otherwise only read
    // those bits. Names of projects, DBs and functions are deliberately not used.
    internal static class CylinderAlarmReset
    {
        private static IEnumerable<XmlElement> C(XmlNode n, string name = null)
        { return n == null ? Enumerable.Empty<XmlElement>() : n.ChildNodes.OfType<XmlElement>().Where(e => name == null || e.LocalName == name); }
        private static IEnumerable<XmlElement> D(XmlNode n, string name)
        { return n.SelectNodes(".//*[local-name()='" + name + "']").Cast<XmlElement>(); }
        private static string A(XmlElement e, string name) { return e == null ? "" : e.GetAttribute(name); }
        private static bool Eq(string a, string b) { return string.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
        private static void Need(bool condition) { if (!condition) throw new InvalidOperationException("Unverified aggregate/reset function"); }
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml),new XmlReaderSettings { XmlResolver=null,DtdProcessing=DtdProcessing.Prohibit,MaxCharactersInDocument=32*1024*1024 })) doc.Load(r);
            return doc;
        }
        private static XmlElement Ancestor(XmlNode n,string name)
        { for(var p=n.ParentNode;p!=null;p=p.ParentNode)if(p.LocalName==name)return (XmlElement)p;return null; }
        private static string Access(XmlElement access)
        {
            if(A(access,"Scope")=="LiteralConstant") {
                var value=D(access,"ConstantValue").Single().InnerText.ToUpperInvariant();
                Need(Regex.IsMatch(value,@"^(TRUE|FALSE|-?\d+)$"));return value;
            }
            Need(A(access,"Scope")=="LocalVariable");
            var symbol=C(access,"Symbol").Single();
            return "$"+string.Concat(C(symbol).Select(SymbolPart));
        }
        private static string SymbolPart(XmlElement part)
        {
            if(part.LocalName=="Blank") return "";
            if(part.LocalName=="Token") {string t=A(part,"Text");Need(new[]{".","[","]",","}.Contains(t));return t;}
            if(part.LocalName=="Access")return Access(part);
            Need(part.LocalName=="Component" && Regex.IsMatch(A(part,"Name"),@"^[\p{L}_][\p{L}\p{N}_]*$"));
            return A(part,"Name")+string.Concat(C(part).Select(SymbolPart));
        }
        private static int[][] Bounds(XmlElement member)
        {
            var m=Regex.Match(A(member,"Datatype"),@"^Array\s*\[([^\]]+)\]\s+of\s+Bool$",RegexOptions.IgnoreCase);Need(m.Success);
            var dimensions=m.Groups[1].Value.Split(',').Select(s=>Regex.Match(s,@"^\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*$")).ToArray();
            Need(dimensions.Length<=2 && dimensions.All(d=>d.Success));
            var result=dimensions.Select(d=>new[]{int.Parse(d.Groups[1].Value),int.Parse(d.Groups[2].Value)}).ToArray();
            Need(result.All(d=>d[0]<=d[1] && (long)d[1]-d[0]<=1024));return result;
        }
        private sealed class Loop { internal string Name; internal int Low,High; }
        private sealed class Checker
        {
            internal readonly List<string> Tokens = new List<string>();
            internal readonly HashSet<string> Inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase), Outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase), Temps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string,int[][]> Fields = new Dictionary<string,int[][]>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Resets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal string Parameter, Target;
            internal int Position;
            private string Take() { Need(Position<Tokens.Count);return Tokens[Position++]; }
            private bool Is(string text) { return Position<Tokens.Count && Eq(Tokens[Position],text); }
            private void Eat(string text) { Need(Eq(Take(),text)); }
            private int Number() { int value;Need(int.TryParse(Take(),out value));return value; }
            private bool Array(string operand,Loop loop,out string field,out string suffix)
            {
                field=suffix="";var m=Regex.Match(operand,@"^\$"+Regex.Escape(Parameter)+@"\.([\p{L}_][\p{L}\p{N}_]*)\[([^\]]+)\]$",RegexOptions.IgnoreCase);
                if(!m.Success)return false;
                field=m.Groups[1].Value;int[][] bounds;Need(Fields.TryGetValue(field,out bounds) && loop!=null);
                var indices=m.Groups[2].Value.Split(',');Need(indices.Length==bounds.Length && Eq(indices[0],loop.Name) && loop.Low==bounds[0][0] && loop.High==bounds[0][1]);
                for(int i=1;i<indices.Length;i++) {int index;Need(int.TryParse(indices[i],out index) && index>=bounds[i][0] && index<=bounds[i][1]);}
                suffix=string.Join(",",indices.Skip(1));return true;
            }
            private bool Expression(IList<string> expression,Loop loop)
            {
                Need(expression.Count>0 && expression.Count%2==1);bool onlyInputs=true;
                for(int i=0;i<expression.Count;i++) {
                    string token=expression[i];
                    if(i%2==1) {Need(Eq(token,"OR") || Eq(token,"AND"));continue;}
                    if(Inputs.Contains(token))continue;
                    onlyInputs=false;string field,suffix;
                    Need(Eq(token,"TRUE") || Eq(token,"FALSE") || Outputs.Contains(token) || Array(token,loop,out field,out suffix));
                }
                return onlyInputs;
            }
            private List<string> Until(string end)
            {var result=new List<string>();while(!Is(end)){result.Add(Take());}Eat(end);return result;}
            internal void Statements(string end,bool resetGuard,Loop loop)
            {
                while(Position<Tokens.Count && !Is(end)) {
                    if(Is("IF")) {
                        Eat("IF");bool guard=Expression(Until("THEN"),loop);
                        Statements("END_IF",resetGuard||guard,loop);Eat("END_IF");Eat(";");
                    } else if(Is("FOR")) {
                        Eat("FOR");string variable=Take();Need(Temps.Contains(variable) && loop==null);Eat(":=");int low=Number();Eat("TO");int high=Number();Eat("BY");Need(Number()==1 && low<=high);Eat("DO");
                        Statements("END_FOR",resetGuard,new Loop{Name=variable,Low=low,High=high});Eat("END_FOR");Eat(";");
                    } else if(Is("EXIT")) {Need(loop!=null && !resetGuard);Eat("EXIT");Eat(";");}
                    else {
                        string lhs=Take();Eat(":=");var rhs=Until(";");
                        if(Outputs.Contains(lhs))Expression(rhs,loop);
                        else {
                            string field,suffix;Need(Array(lhs,loop,out field,out suffix) && resetGuard && rhs.Count==1 && Eq(rhs[0],"FALSE"));
                            if(Eq(field,Target))Resets.Add(suffix);
                        }
                    }
                }
            }
        }
        private static bool Callee(XmlDocument doc,XmlDocument db,string parameter,string array)
        {
            var checker=new Checker { Parameter=parameter,Target=array };
            var sections=D(doc,"Interface").SelectMany(i=>C(i,"Sections")).SelectMany(s=>C(s,"Section")).ToArray();
            var inout=sections.Where(s=>A(s,"Name")=="InOut").SelectMany(s=>C(s,"Member")).ToArray();
            Need(inout.Length==1 && Eq(A(inout[0],"Name"),parameter) && A(inout[0],"Datatype")=="Struct");
            foreach(var s in sections)foreach(var m in C(s,"Member")) {
                string kind=A(s,"Name");
                if(kind=="Input" || kind=="Output") {Need(Eq(A(m,"Datatype"),"Bool"));(kind=="Input"?checker.Inputs:checker.Outputs).Add("$"+A(m,"Name"));}
                else if(kind=="Temp") {Need(Eq(A(m,"Datatype"),"Int"));checker.Temps.Add("$"+A(m,"Name"));}
                else Need(kind=="InOut" || kind=="Return" && Eq(A(m,"Datatype"),"Void"));
            }
            Need(checker.Inputs.Count>0 && checker.Outputs.Count>0);
            var dbMembers=D(db,"Section").Where(s=>A(s,"Name")=="Static").SelectMany(s=>C(s,"Member")).ToArray();
            foreach(var member in C(inout[0],"Member")) {
                string name=A(member,"Name");var actual=dbMembers.Single(m=>Eq(A(m,"Name"),name));var expected=Bounds(member);var found=Bounds(actual);
                Need(expected.Length==found.Length && expected.Zip(found,(a,b)=>a.SequenceEqual(b)).All(v=>v));checker.Fields.Add(name,found);
            }
            Need(checker.Fields.ContainsKey(array));
            var sources=D(doc,"NetworkSource").ToArray();Need(sources.Length>0);
            foreach(var network in sources) {
                Need(C(network).Count()==1 && C(network,"StructuredText").Count()==1);
                foreach(var item in C(C(network,"StructuredText").Single())) {
                    if(new[]{"Blank","NewLine","LineComment","Comment"}.Contains(item.LocalName))continue;
                    if(item.LocalName=="Access")checker.Tokens.Add(Access(item));
                    else {Need(item.LocalName=="Token");checker.Tokens.Add(A(item,"Text"));}
                }
            }
            checker.Statements("",false,null);Need(checker.Position==checker.Tokens.Count);
            var target=checker.Fields[array];
            return target.Length==1 ? checker.Resets.SetEquals(new[]{""}) : checker.Resets.SetEquals(Enumerable.Range(target[1][0],target[1][1]-target[1][0]+1).Select(n=>n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        internal static bool IsSharedReset(string dbName,string arrayName,IndexEntry[] blocks,Func<IndexEntry,string> source)
        {
            try {
                var dbEntry=blocks.Single(b=>b.language=="DB" && Eq(b.name,dbName));var db=Parse(source(dbEntry));bool found=false;
                foreach(var entry in blocks.Where(b=>b.language!="DB" && b.unit_name==dbEntry.unit_name)) {
                    var document=Parse(source(entry));
                    foreach(var access in D(document,"Access").Where(a=>A(a,"Scope")=="GlobalVariable")) {
                        var components=C(C(access,"Symbol").FirstOrDefault(),"Component").ToArray();
                        if(components.Length!=1 || !Eq(A(components[0],"Name"),dbName))continue;
                        Need(!C(components[0]).Any());var net=Ancestor(access,"FlgNet");Need(net!=null && access.ParentNode.LocalName=="Parts");
                        var uses=D(net,"Wire").Where(w=>C(w,"IdentCon").Any(i=>A(i,"UId")==A(access,"UId"))).ToArray();Need(uses.Length>0);
                        foreach(var wire in uses) {
                            Need(C(wire).Count()==2 && C(wire,"IdentCon").Count()==1 && C(wire,"NameCon").Count()==1);
                            var connector=C(wire,"NameCon").Single();var call=C(access.ParentNode,"Call").Single(c=>A(c,"UId")==A(connector,"UId"));var info=C(call,"CallInfo").Single();
                            string parameter=A(connector,"Name");Need(A(info,"BlockType")=="FC" && C(info,"Parameter").Any(p=>A(p,"Name")==parameter && A(p,"Section")=="InOut"));
                            var callee=blocks.Single(b=>b.language!="DB" && b.unit_name==entry.unit_name && Eq(b.name,A(info,"Name")));
                            Need(Callee(Parse(source(callee)),db,parameter,arrayName));found=true;
                        }
                    }
                }
                return found;
            }catch(Exception ex) {if(ex is OutOfMemoryException || ex is StackOverflowException)throw;return false;}
        }
    }
}
