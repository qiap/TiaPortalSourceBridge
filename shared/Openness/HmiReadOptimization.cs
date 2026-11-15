using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Openness
{
    // Lifetime is one request, never a project-wide or cross-session cache.
    internal sealed class HmiReadCache
    {
        private readonly Dictionary<object,string> values = new Dictionary<object,string>();
        internal string Get(object key, Func<string> read)
        {
            string value;
            if (values.TryGetValue(key, out value)) { OperationMetrics.Count("hmi_export_cache_hits"); return value; }
            value = read(); values.Add(key, value); return value;
        }
        internal void Clear() { values.Clear(); }
    }

    internal sealed partial class HmiOperations
    {
        private readonly HmiReadCache readCache = new HmiReadCache();
        private bool cacheReads;
        internal void InvalidateReadCache() { readCache.Clear(); }
        internal IDisposable BeginReadScope() { return new ReadScope(this); }
        private sealed class ReadScope : IDisposable
        {
            private readonly HmiOperations owner; private readonly bool previous;
            internal ReadScope(HmiOperations owner) { this.owner=owner; previous=owner.cacheReads; if(!previous)owner.readCache.Clear(); owner.cacheReads=true; }
            public void Dispose() { owner.cacheReads=previous; if(!previous)owner.readCache.Clear(); }
        }
        internal static bool NeedsCompile(IDictionary<string,object> result)
        { object n; return result.TryGetValue("changed_count",out n) && n!=null && Convert.ToInt32(n)>0; }
    }

    internal static class HmiXmlIdentity
    {
        internal static string Canonical(string xml)
        {
            var doc=HmiValidation.Parse(xml);
            // Parse separately with whitespace retained: user text and rich text are significant.
            doc.PreserveWhitespace=true; doc.LoadXml(xml);
            var root=doc.SelectSingleNode("/Document/*[starts-with(local-name(),'Hmi.')]");
            if(root==null)throw new ArgumentException("Missing HMI object");
            var ids=new Dictionary<string,string>(StringComparer.Ordinal); int index=0;
            foreach(XmlElement e in root.SelectNodes("descendant-or-self::*[@ID]")) {
                string id=e.GetAttribute("ID");
                if(ids.ContainsKey(id))return root.OuterXml; // ambiguous IDs: do not normalize
                ids.Add(id,(index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach(XmlElement e in root.SelectNodes("descendant-or-self::*[@TargetID]")) {
                string id=e.GetAttribute("TargetID");
                if(!id.StartsWith("@",StringComparison.Ordinal)&&!ids.ContainsKey(id))return root.OuterXml;
            }
            foreach(XmlElement e in root.SelectNodes("descendant-or-self::*")) {
                if(e.HasAttribute("ID"))e.SetAttribute("ID",ids[e.GetAttribute("ID")]);
                if(e.HasAttribute("TargetID")&&ids.ContainsKey(e.GetAttribute("TargetID")))e.SetAttribute("TargetID",ids[e.GetAttribute("TargetID")]);
                if(e.SelectSingleNode("ancestor-or-self::Text")!=null)continue;
                if(e.ChildNodes.Cast<XmlNode>().Any(n=>n is XmlElement))
                    foreach(var n in e.ChildNodes.Cast<XmlNode>().Where(n=>n.NodeType==XmlNodeType.Whitespace).ToArray())e.RemoveChild(n);
            }
            return root.OuterXml;
        }
        internal static bool Equal(string a,string b) { return string.Equals(Canonical(a),Canonical(b),StringComparison.Ordinal); }
    }
}
