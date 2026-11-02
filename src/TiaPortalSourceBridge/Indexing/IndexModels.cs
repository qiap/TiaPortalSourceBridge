using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed class IndexEntry
    {
        public string id { get; set; }
        public string kind { get; set; }
        public string owner { get; set; }
        public string group { get; set; }
        public string name { get; set; }
        public string language { get; set; }
        public string number { get; set; }
        public string state { get; set; }
        public string content_hash { get; set; }
        public string indexed_utc { get; set; }
        public string error { get; set; }
        public string fingerprint { get; set; }
        public IndexEntry() { state = "catalog_only"; content_hash = indexed_utc = error = fingerprint = ""; }
        internal static IndexEntry Create(string kind, string owner, string group, string name, string language = "", string number = "")
        {
            return new IndexEntry { id = IndexJson.Hash(kind + "\n" + owner + "\n" + group + "\n" + name), kind = kind, owner = owner, group = group, name = name, language = language, number = number };
        }
    }
    internal sealed class IndexFact
    {
        public string kind { get; set; }
        public string location { get; set; }
        public string text { get; set; }
    }
    internal sealed class IndexReference
    {
        public string relation { get; set; }
        public string target { get; set; }
        public string location { get; set; }
        public string evidence { get; set; }
    }
    internal sealed class IndexContent
    {
        public string hash { get; set; }
        public string search_text { get; set; }
        public string parser { get; set; }
        public string object_name { get; set; }
        public string source_version { get; set; }
        public List<IndexFact> facts { get; set; }
        public List<IndexReference> references { get; set; }
        public List<string> warnings { get; set; }
        public List<string> definitions { get; set; }
        public IndexContent() { facts = new List<IndexFact>(); references = new List<IndexReference>(); warnings = new List<string>(); definitions = new List<string>(); parser = "structure-v1"; }
    }
    internal sealed class IndexCatalog
    {
        public int schema { get; set; }
        public string project_file { get; set; }
        public string portal_version { get; set; }
        public string built_utc { get; set; }
        public List<IndexEntry> objects { get; set; }
        public List<string> warnings { get; set; }
        public IndexCatalog() { schema = 1; objects = new List<IndexEntry>(); warnings = new List<string>(); }
    }
    internal sealed class IndexDirty
    {
        public bool all { get; set; }
        public List<string> names { get; set; }
        public string reason { get; set; }
        public IndexDirty() { names = new List<string>(); }
    }
    internal static class IndexJson
    {
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 128 }; }
        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        internal static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)Serializer().DeserializeObject(Serializer().Serialize(value)); }
        internal static string Text(Dictionary<string, object> map, string key) { object value; return map.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
    }
}
