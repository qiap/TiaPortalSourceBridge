using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class CylinderTemplateContext
    {
        public string project_path, plc_name, host_id, host_name, group_path, unit_name, sample_id, fb, sample_fingerprint;
        public int sample_network;
        public string[] parameters;
    }

    // Stores only reviewed role metadata. It cannot write a TIA project or import bindings.
    internal static class CylinderTemplateStore
    {
        private static readonly string[] Roles = { "shared", "device", "index", "input", "output", "open", "feedback" };
        private static XmlElement Element(XmlNode parent, string name)
        { return parent == null ? null : parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name); }
        private static string Fingerprint(XmlElement network, XmlElement instance, XmlElement fbInterface)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml("<Document><SW.Blocks.FB><AttributeList><Name>TemplateSample</Name><Interface><Sections><Section Name='Static'/></Sections></Interface></AttributeList><ObjectList/></SW.Blocks.FB></Document>");
            doc.SelectSingleNode("//Section").AppendChild(doc.ImportNode(instance, true));
            doc.SelectSingleNode("//ObjectList").AppendChild(doc.ImportNode(network, true));
            string sample = XmlBatch.RecoverySignature(doc.OuterXml);
            doc.LoadXml("<Document><SW.Blocks.FB><AttributeList><Name>TemplateInterface</Name></AttributeList></SW.Blocks.FB></Document>");
            doc.SelectSingleNode("//AttributeList").AppendChild(doc.ImportNode(fbInterface, true));
            return XmlBatch.Hash(sample + "\n" + XmlBatch.RecoverySignature(doc.OuterXml));
        }
        internal static CylinderTemplateContext Context(IoSnapshot snapshot, IndexEntry host, Func<IndexEntry, string> source, string sample, int network)
        {
            if (snapshot == null || host == null || string.IsNullOrWhiteSpace(sample) || sample.Length > 128 || network < 1)
                throw new InvalidOperationException("请选择当前工程中的宿主块和气缸样板。");
            var doc = BlockOperations.ParseBlock(source(host));
            var block = doc.SelectSingleNode("/Document/SW.Blocks.FB");
            if (block == null) throw new InvalidOperationException("气缸模板需要 FB 宿主块。");
            var units = block.SelectNodes(".//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();
            if (network > units.Length) throw new InvalidOperationException("样板网络已改变，请重新读取。");
            var selected = units[network - 1];
            var calls = selected.SelectNodes(".//*[local-name()='CallInfo']").Cast<XmlElement>().ToArray();
            if (calls.Length != 1 || calls[0].GetAttribute("BlockType") != "FB") throw new InvalidOperationException("样板必须是可独立复用的单个 FB 调用。");
            var call = calls[0]; var instance = Element(call, "Instance");
            var components = instance == null ? new XmlElement[0] : instance.ChildNodes.OfType<XmlElement>().ToArray();
            if (instance == null || instance.GetAttribute("Scope") != "LocalVariable" || components.Length != 1 ||
                components[0].LocalName != "Component" || components[0].GetAttribute("Name") != sample)
                throw new InvalidOperationException("样板实例已改变，请重新选择。");
            string fb = call.GetAttribute("Name");
            var declarations = block.SelectNodes("./AttributeList/*[local-name()='Interface']/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']/*[local-name()='Member']")
                .Cast<XmlElement>().Where(m => m.GetAttribute("Name") == sample).ToArray();
            if (declarations.Length != 1 || !string.Equals(declarations[0].GetAttribute("Datatype").Trim('"'), fb, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("样板的实例声明与被调用 FB 不一致。");
            var definitions = (snapshot.cylinderBlocks ?? new IndexEntry[0]).Where(b => b.unit_name == host.unit_name && string.Equals(b.name, fb, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (definitions.Length != 1) throw new InvalidOperationException("未完整读取样板所调用的 FB 接口，请从博途重新读取。");
            var definition = BlockOperations.ParseBlock(source(definitions[0]));
            var fbInterface = definition.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/*[local-name()='Interface']") as XmlElement;
            if (fbInterface == null) throw new InvalidOperationException("无法核对被调用 FB 的接口。");
            var parameters = call.ChildNodes.OfType<XmlElement>().Where(p => p.LocalName == "Parameter").Select(p => p.GetAttribute("Name")).ToArray();
            if (parameters.Length == 0 || parameters.Any(string.IsNullOrWhiteSpace) || parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length)
                throw new InvalidOperationException("样板参数无效。");
            return new CylinderTemplateContext { project_path = Path.GetFullPath(snapshot.project_path), plc_name = snapshot.plc_name,
                host_id = host.id, host_name = host.name, group_path = host.group ?? "", unit_name = host.unit_name ?? "",
                sample_id = sample, sample_network = network, fb = fb, parameters = parameters,
                sample_fingerprint = Fingerprint(selected, declarations[0], fbInterface) };
        }
        internal static string PathFor(string root, CylinderTemplateContext context)
        {
            string project = XmlBatch.Hash(Path.GetFullPath(context.project_path).ToUpperInvariant());
            string scope = XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(new[] { context.plc_name, context.unit_name, context.group_path,
                context.host_name, context.sample_id, context.sample_network.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            return Path.Combine(Path.GetFullPath(root), "project-templates", project, scope + ".json");
        }
        internal static Dictionary<string, object> Read(string root, CylinderTemplateContext context)
        {
            string path = PathFor(root, context);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("保存的模板过大，请重新核对并保存。");
            var template = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (template == null || CylinderWritePipeline.Text(template, "kind") != "cylinder-template-rules" ||
                CylinderWritePipeline.Text(template, "schema") != "1" ||
                !string.Equals(CylinderWritePipeline.Text(template, "project_path"), context.project_path, StringComparison.OrdinalIgnoreCase) ||
                CylinderWritePipeline.Text(template, "plc_name") != context.plc_name || CylinderWritePipeline.Text(template, "host_name") != context.host_name ||
                CylinderWritePipeline.Text(template, "group_path") != context.group_path || CylinderWritePipeline.Text(template, "unit_name") != context.unit_name ||
                CylinderWritePipeline.Text(template, "sample_id") != context.sample_id || CylinderWritePipeline.Text(template, "sample_network") != context.sample_network.ToString())
                throw new InvalidOperationException("保存的模板身份不一致，请重新核对。");
            return template;
        }
        internal static Dictionary<string, object> Save(string root, CylinderTemplateContext context, string fingerprint, Dictionary<string, object> rules, bool reviewed)
        {
            if (!reviewed || fingerprint != context.sample_fingerprint) throw new InvalidOperationException("样板已变化或尚未确认，请重新核对当前规则。");
            if (rules == null || rules.Count != context.parameters.Length || context.parameters.Any(p => !rules.ContainsKey(p)) ||
                rules.Any(p => !(p.Value is string) || !Roles.Contains((string)p.Value)) || !rules.Values.Contains("output"))
                throw new InvalidOperationException("模板必须为当前全部参数指定有效规则，并包含阀输出。");
            var template = new Dictionary<string, object> { { "schema", 1 }, { "kind", "cylinder-template-rules" },
                { "project_path", context.project_path }, { "plc_name", context.plc_name }, { "host_name", context.host_name },
                { "group_path", context.group_path }, { "unit_name", context.unit_name }, { "sample_id", context.sample_id },
                { "sample_network", context.sample_network }, { "fb", context.fb }, { "sample_fingerprint", context.sample_fingerprint },
                { "rules", new Dictionary<string, object>(rules) }, { "updated", DateTime.UtcNow.ToString("o") } };
            string path = PathFor(root, context), temp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temp, CylinderWritePipeline.Json().Serialize(template), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            return template;
        }
    }

    internal sealed partial class WebServer
    {
        private bool HandleCylinderTemplates(HttpListenerContext context, string path)
        {
            if (path != "/api/io/cylinder-templates") return false;
            var request = context.Request; var response = context.Response;
            bool save = request.HttpMethod == "POST";
            if (!save && request.HttpMethod != "GET") { SendJson(response, 405, new { error = "仅支持读取或保存模板规则" }); return true; }
            Dictionary<string, object> map;
            if (save) {
                if (request.ContentLength64 < 0 || request.ContentLength64 > 65536) { SendJson(response, 413, new { error = "模板规则过大" }); return true; }
                try { map = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(ReadBody(request)); }
                catch (ArgumentException) { SendJson(response, 400, new { error = "模板规则格式无效" }); return true; }
                if (map == null) { SendJson(response, 400, new { error = "缺少模板规则" }); return true; }
            } else map = new Dictionary<string, object> { { "id", GetQueryParam(request, "id") }, { "host", GetQueryParam(request, "host") },
                { "sample_id", GetQueryParam(request, "sample_id") }, { "sample_network", GetQueryParam(request, "sample_network") } };
            lock (ioGate) {
                IoJob found; int network;
                if (!ioJobs.TryGetValue(Text(map, "id"), out found) || found.state != "completed" || found.result == null ||
                    !int.TryParse(Text(map, "sample_network"), out network)) {
                    SendJson(response, 409, new { error = "源码快照已过期，请重新读取并选择样板" }); return true;
                }
                try {
                    var host = found.result.cylinderBlocks.SingleOrDefault(b => b.id == Text(map, "host"));
                    var store = new ProjectIndexStore(found.project, AppConfig.DetectProjectVersion(found.project));
                    var identity = CylinderTemplateStore.Context(found.result, host, store.IoSource, Text(map, "sample_id"), network);
                    string root = Path.GetDirectoryName(Path.GetFullPath(_configPath));
                    Dictionary<string, object> template;
                    if (save) {
                        object value; map.TryGetValue("rules", out value);
                        template = CylinderTemplateStore.Save(root, identity, Text(map, "sample_fingerprint"), value as Dictionary<string, object>, CylinderWritePipeline.True(map, "reviewed"));
                    } else template = CylinderTemplateStore.Read(root, identity);
                    SendJson(response, 200, new { context = identity, template, matches = template != null && Text(template, "sample_fingerprint") == identity.sample_fingerprint });
                } catch (Exception ex) { SendJson(response, 409, new { error = ex.Message }); }
            }
            return true;
        }
    }
}
