using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class XmlBatchItem
    {
        public string block_name { get; set; }
        public string group_path { get; set; }
        public string backup { get; set; }
        public string status { get; set; }
        public string verification { get; set; }
        public string error { get; set; }
        public string expected_hash { get; set; }
        public string expected_content_signature { get; set; }
        public string expected_recovery_signature { get; set; }
        public string before_hash { get; set; }
        public string after_hash { get; set; }
        public string after_recovery_signature { get; set; }
        public string[] differences { get; set; }
        internal string Xml;
        [ScriptIgnore] public string index_xml { get; set; }
        internal string Before;
        internal CachedBlockEntry Entry;
    }

    internal sealed class XmlBatchRelatedItem
    {
        public string block_name { get; set; }
        public string group_path { get; set; }
        public string expected_recovery_signature { get; set; }
        public string backup { get; set; }
        public string before_signature { get; set; }
        public string after_signature { get; set; }
        public string after_hash { get; set; }
        public string after_path { get; set; }
        public string status { get; set; }
        public string error { get; set; }
        internal CachedBlockEntry Entry;
        internal string Before;
    }

    internal sealed class XmlBatchResult
    {
        public bool saved { get { return false; } }
        public bool ok { get; set; }
        public bool compiled { get; set; }
        public int? error_count { get; set; }
        public int? warning_count { get; set; }
        public string compile_report { get; set; }
        public string phase { get; set; }
        public string current_block { get; set; }
        public string error { get; set; }
        public string journal { get; set; }
        public XmlBatchItem[] items { get; set; }
        public XmlBatchRelatedItem[] related_items { get; set; }
    }

    // Pure orchestration: every backup completes before the first mutation.
    // Imports are not transactional; stop on the first failure and retain evidence.
    internal static class XmlBatch
    {
        internal static string CreateJournalDirectory(string backupRoot, string operationId)
        {
            Guid operation;
            bool identified = !string.IsNullOrWhiteSpace(operationId);
            if (identified && (!Guid.TryParse(operationId, out operation) || operation == Guid.Empty))
                throw new ArgumentException("批次操作编号无效。");
            operation = identified ? Guid.Parse(operationId) : Guid.NewGuid();
            string directory = Path.Combine(backupRoot, identified ? "batch-operation-" + operation.ToString("N")
                : "batch-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + operation.ToString("N"));
            if (Directory.Exists(directory)) throw new InvalidOperationException("该操作编号已有批次记录，拒绝重复执行；请先查询原任务结果。");
            Directory.CreateDirectory(directory);
            // Different projects may share a backup directory. CreateNew is the
            // cross-project claim; never truncate an existing operation's evidence.
            using (var claim = new FileStream(Path.Combine(directory, "manifest.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(claim, new UTF8Encoding(false))) writer.Write(operation.ToString("N"));
            return directory;
        }
        internal static XmlBatchRelatedItem[] ParseRelated(string json, XmlBatchItem[] items)
        {
            if (string.IsNullOrWhiteSpace(json)) return new XmlBatchRelatedItem[0];
            var rows = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(json) as object[];
            if (rows == null || rows.Length > 500) throw new ArgumentException("related_blocks_json 必须为最多 500 个现有块组成的数组。");
            var names = new HashSet<string>(items.Select(i => i.block_name), StringComparer.OrdinalIgnoreCase);
            return rows.Select(row => {
                var map = row as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("每个关联块必须为对象。");
                Func<string, string> get = key => map.ContainsKey(key) ? Convert.ToString(map[key]) : "";
                string name = get("block_name").Trim(), signature = get("expected_recovery_signature");
                if (name.Length == 0 || !names.Add(name)) throw new ArgumentException("关联块名称为空、重复或已列入写入项。");
                if (signature.Length != 64 || !signature.All(Uri.IsHexDigit)) throw new ArgumentException("关联块缺少有效的完整恢复签名：" + name);
                return new XmlBatchRelatedItem { block_name = name, group_path = get("group_path"),
                    expected_recovery_signature = signature, status = "pending" };
            }).ToArray();
        }
        internal static void ValidateRelatedBefore(XmlBatchRelatedItem item, string xml)
        {
            var doc = BlockOperations.ParseBlock(xml);
            var name = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]/AttributeList/Name");
            if (name == null || !string.Equals(name.InnerText.Trim(), item.block_name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("关联块身份与请求不一致：" + item.block_name);
            string signature = RecoverySignature(xml);
            if (!string.Equals(signature, item.expected_recovery_signature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("关联块内容、标题或注释已变化；尚未写入：" + item.block_name);
            item.Before = xml; item.before_signature = signature; item.status = "validated";
        }
        internal static XmlBatchItem[] Parse(string json)
        {
            var rows = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(json) as object[];
            if (rows == null || rows.Length == 0 || rows.Length > 50)
                throw new ArgumentException("items_json 必须包含 1～50 个现有块。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return rows.Select(row =>
            {
                var map = row as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("每项必须为对象。");
                Func<string, string> get = key => map.ContainsKey(key) ? Convert.ToString(map[key]) : "";
                string xml = get("xml");
                var doc = BlockOperations.ParseBlock(xml);
                BlockOperations.ValidateCompileUnitOrder(doc);
                var name = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]/AttributeList/Name");
                if (name == null || string.IsNullOrWhiteSpace(name.InnerText) || !names.Add(name.InnerText.Trim()))
                    throw new ArgumentException("块名称为空或批次中重复。");
                if (doc.SelectSingleNode("//*[local-name()='Graph']") != null)
                    GraphEditor.ValidateDocument(doc);
                return new XmlBatchItem { block_name = name.InnerText.Trim(), group_path = get("group_path"),
                    expected_hash = get("expected_hash"), expected_content_signature = get("expected_content_signature"), expected_recovery_signature = get("expected_recovery_signature"), Xml = xml, status = "pending", verification = "not_run" };
            }).ToArray();
        }

        internal static XmlBatchResult Run(XmlBatchItem[] items, Action<XmlBatchItem> preflight,
            Action<XmlBatchItem> backup, Action<XmlBatchItem> import, Func<CompileSummary> compile,
            Action<XmlBatchItem> verify, Action<XmlBatchResult> persist)
        {
            var result = new XmlBatchResult { items = items, phase = "preflight" };
            XmlBatchItem current = null;
            bool attempted = false;
            try
            {
                persist(result);
                foreach (var item in items) { current = item; result.current_block = item.block_name; persist(result); preflight(item); item.status = "validated"; persist(result); }
                result.phase = "backup"; result.current_block = ""; persist(result);
                foreach (var item in items) { current = item; result.current_block = item.block_name; persist(result); backup(item); item.status = "backed_up"; persist(result); }
                result.phase = "import"; result.current_block = ""; persist(result);
                foreach (var item in items)
                {
                    current = item; result.current_block = item.block_name; item.status = "importing"; persist(result);
                    attempted = true; import(item); item.status = "imported"; persist(result);
                }
            }
            catch (Exception ex)
            {
                result.error = result.phase + ": " + ex.Message;
                if (current != null) { current.error = ex.Message; current.status = attempted && result.phase == "import" ? "outcome_unknown" : "failed"; }
            }
            // One compile even after a partial import, since TIA may have changed the failing block.
            if (attempted)
            {
                result.phase = "compile"; result.current_block = ""; persist(result);
                try
                {
                    var summary = compile(); result.compiled = true;
                    result.error_count = summary.ErrorCount; result.warning_count = summary.WarningCount;
                    result.compile_report = summary.ToReport();
                }
                catch (Exception ex) { result.error = (result.error ?? "") + " compile: " + ex.Message; }
                result.phase = "verify"; persist(result);
                foreach (var item in items.Where(i => i.status == "imported" || i.status == "outcome_unknown"))
                {
                    result.current_block = item.block_name; persist(result);
                    try { verify(item); }
                    catch (Exception ex) { item.verification = "failed"; item.error = (item.error ?? "") + " readback: " + ex.Message; }
                    persist(result);
                }
            }
            result.ok = result.error == null && result.compiled && result.error_count == 0
                && items.All(i => i.status == "imported" && i.verification == "matched");
            result.phase = result.ok ? "complete" : "needs_review";
            result.current_block = "";
            persist(result);
            return result;
        }

        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        // Conservative comparison, not a proof of program equivalence. Unknown differences
        // remain visible. Keep UId (including wire references) so rewiring cannot disappear.
        internal static string ContentSignature(string xml)
        {
            return Hash(string.Join("\n", ContentSections(xml).Select(p => p.Key + ":" + Hash(p.Value))));
        }

        // Recovery replaces the complete object, including titles, comments and
        // interface properties. Its concurrency guard must cover that same scope.
        internal static string RecoverySignature(string xml)
        {
            var doc = BlockOperations.ParseBlock(xml);
            NormalizeNetworkIds(doc);
            var block = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
            if (block == null) throw new ArgumentException("恢复检查需要完整程序块。");
            return Hash(Canonical(block));
        }

        internal static string[] Differences(string requested, string actual)
        {
            var a = ContentSections(requested); var b = ContentSections(actual);
            return a.Keys.Union(b.Keys).Where(k => !a.ContainsKey(k) || !b.ContainsKey(k) || a[k] != b[k]).ToArray();
        }

        private static Dictionary<string, string> ContentSections(string xml)
        {
            var doc = BlockOperations.ParseBlock(xml);
            NormalizeNetworkIds(doc);
            var selected = doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.Blocks.')]/AttributeList/* | //SW.Blocks.CompileUnit/AttributeList/*");
            var parts = new Dictionary<string, string>();
            foreach (XmlNode node in selected)
            {
                if (node.LocalName == "Interface" || node.LocalName == "NetworkSource" || node.LocalName == "Name"
                    || node.LocalName == "Number" || node.LocalName == "ProgrammingLanguage")
                    parts.Add(node.LocalName + "[" + parts.Keys.Count(k => k.StartsWith(node.LocalName + "[", StringComparison.Ordinal)) + "]", Canonical(node));
            }
            return parts;
        }

        private static string Canonical(XmlNode node)
        {
            var e = node as XmlElement;
            if (e == null) return node is XmlText ? node.Value : "";
            if (e.LocalName == "Blank" || e.LocalName == "NewLine") return "";
            if (e.LocalName == "Action" && !e.HasChildNodes && e.Attributes.Count == 0) return "";
            if (e.LocalName == "BooleanAttribute" && e.GetAttribute("Name") == "SetPoint" && e.InnerText == "false") return "";
            bool structuredText = e.SelectSingleNode("ancestor-or-self::*[local-name()='StructuredText']") != null;
            var attrs = e.Attributes.Cast<XmlAttribute>().Where(a => a.Name != "ID" && !(structuredText && a.Name == "UId") && a.Prefix != "xmlns" && a.Name != "xmlns"
                && !(a.Name == "IsMissing" && a.Value == "false")).OrderBy(a => a.Name)
                .Select(a => a.Name + "=" + a.Value).ToArray();
            var children = e.ChildNodes.Cast<XmlNode>().Select(Canonical).Where(s => s.Length > 0).ToList();
            if (e.LocalName == "Parts" || e.LocalName == "Member" || e.LocalName == "AttributeList") children.Sort(StringComparer.Ordinal);
            if (e.NamespaceURI.Contains("/Graph/") && new[] { "Steps", "Transitions", "Branches", "Connections" }.Contains(e.LocalName))
                children.Sort(StringComparer.Ordinal);
            // Length-prefix each value instead of recursively JSON-escaping child strings.
            // GRAPH trees are deep; repeated escaping would grow exponentially.
            var text = new StringBuilder();
            foreach (string value in new[] { e.NamespaceURI, e.LocalName, attrs.Length.ToString() }.Concat(attrs).Concat(new[] { children.Count.ToString() }).Concat(children))
                text.Append(value.Length).Append(':').Append(value);
            return text.ToString();
        }

        private static void NormalizeNetworkIds(XmlDocument doc)
        {
            foreach (XmlElement net in doc.SelectNodes("//*[local-name()='FlgNet']"))
            {
                var parts = net.SelectSingleNode("*[local-name()='Parts']");
                if (parts == null) continue;
                // TIA places Access definitions before Parts and terminal coils last.
                // Normalize IDs AND references together, never discard wire endpoints.
                var definitions = parts.ChildNodes.OfType<XmlElement>().OrderBy(e => e.LocalName == "Access" ? 0 :
                    e.GetAttribute("Name") == "TrCoil" || e.GetAttribute("Name") == "Coil" ? 2 : 1).ToArray();
                var ids = new Dictionary<string, string>();
                foreach (var definition in definitions)
                    foreach (XmlElement node in definition.SelectNodes("descendant-or-self::*[@UId]"))
                    {
                        string id = node.GetAttribute("UId");
                        if (!ids.ContainsKey(id)) ids.Add(id, "p" + ids.Count);
                    }
                foreach (XmlElement node in net.SelectNodes(".//*[@UId]"))
                {
                    string id = node.GetAttribute("UId");
                    if (node.LocalName == "Wire") node.RemoveAttribute("UId");
                    else if (ids.ContainsKey(id)) node.SetAttribute("UId", ids[id]);
                }
            }
        }
    }
}
