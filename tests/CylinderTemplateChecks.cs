using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class CylinderTemplateChecks
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, message); }
    static string Network(string sample, string pin = "Sense") { return "<SW.Blocks.CompileUnit ID='10'><AttributeList><NetworkSource><FlgNet xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4'><Parts><Access UId='1' Scope='GlobalVariable'><Symbol><Component Name='Sensor'/></Symbol></Access><Call UId='2'><CallInfo Name='Cylinder.FB' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='" + sample + "'/></Instance><Parameter Name='" + pin + "' Section='Input' Type='Bool'/><Parameter Name='Valve' Section='Output' Type='Bool'/></CallInfo></Call></Parts><Wires><Wire UId='3'><IdentCon UId='1'/><NameCon UId='2' Name='" + pin + "'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    static string Host(string extra = "", string member = "", string selected = null) { return "<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Host</Name><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Static'><Member Name='Z01' Datatype='&quot;Cylinder.FB&quot;'/>" + member + "</Section></Sections></Interface></AttributeList><ObjectList>" + (selected ?? Network("Z01")) + extra + "</ObjectList></SW.Blocks.FB></Document>"; }
    static string Definition(string type = "Bool") { return "<Document><SW.Blocks.FB><AttributeList><Name>Cylinder.FB</Name><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Input'><Member Name='Sense' Datatype='" + type + "'/></Section><Section Name='Output'><Member Name='Valve' Datatype='Bool'/></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>"; }
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "cylinder-template-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var host = new IndexEntry { id = "host", name = "Host", group = "Cylinder", unit_name = "" };
            var fb = new IndexEntry { id = "fb", name = "Cylinder.FB", group = "Library", unit_name = "" };
            var snapshot = new IoSnapshot { project_path = Path.Combine(root, "Project.ap17"), plc_name = "Station/CPU", cylinderBlocks = new[] { host, fb } };
            string hostXml = Host(), fbXml = Definition();
            Func<CylinderTemplateContext> context = () => CylinderTemplateStore.Context(snapshot, host, entry => entry.id == "host" ? hostXml : fbXml, "Z01", 1);
            var original = context();
            Check(original.sample_fingerprint.Length == 64, "server derives full fingerprint");
            hostXml = Host(Network("Z02"), "<Member Name='Z02' Datatype='&quot;Cylinder.FB&quot;'/>");
            Check(context().sample_fingerprint == original.sample_fingerprint, "other host network/instance does not invalidate sample");
            hostXml = Host().Replace("UId='1'", "UId='71'").Replace("UId='2'", "UId='72'").Replace("UId='3'", "UId='73'");
            Check(context().sample_fingerprint == original.sample_fingerprint, "volatile network IDs normalize together");
            hostXml = Host(selected: Network("Z01").Replace("Name='Sensor'", "Name='OtherSensor'"));
            Check(context().sample_fingerprint != original.sample_fingerprint, "binding change requires review");
            hostXml = Host(selected: Network("Z01", "ChangedSense"));
            Check(context().sample_fingerprint != original.sample_fingerprint, "pin change requires review");
            hostXml = Host().Replace("Datatype='&quot;Cylinder.FB&quot;'/>", "Datatype='&quot;Cylinder.FB&quot;'><StartValue>changed</StartValue></Member>");
            Check(context().sample_fingerprint != original.sample_fingerprint, "selected instance declaration change requires review");
            hostXml = Host(); fbXml = Definition("Int");
            Check(context().sample_fingerprint != original.sample_fingerprint, "FB interface change requires review");
            fbXml = Definition();
            var rules = new Dictionary<string, object> { { "Sense", "input" }, { "Valve", "output" } };
            Check(CylinderTemplateStore.Read(root, original) == null, "missing template returns empty");
            Reject(() => CylinderTemplateStore.Save(root, original, original.sample_fingerprint, rules, false), "unreviewed save rejected");
            Reject(() => CylinderTemplateStore.Save(root, original, new string('a', 64), rules, true), "stale fingerprint rejected");
            var bad = new Dictionary<string, object>(rules); bad["Sense"] = "any code";
            Reject(() => CylinderTemplateStore.Save(root, original, original.sample_fingerprint, bad, true), "unknown roles rejected");
            bad = new Dictionary<string, object>(rules); bad["Extra"] = "shared";
            Reject(() => CylinderTemplateStore.Save(root, original, original.sample_fingerprint, bad, true), "extra parameters rejected");
            var saved = CylinderTemplateStore.Save(root, original, original.sample_fingerprint, rules, true);
            Check(!saved.ContainsKey("bindings") && !saved.ContainsKey("xml") && !saved.ContainsKey("call"), "only role metadata is persisted");
            var loaded = CylinderTemplateStore.Read(root, original);
            Check((string)loaded["sample_fingerprint"] == original.sample_fingerprint, "saved template survives new read");
            string savedPath = CylinderTemplateStore.PathFor(root, original);
            hostXml = Host(Network("Z02"));
            Check((string)CylinderTemplateStore.Read(root, context())["sample_fingerprint"] == original.sample_fingerprint, "template survives added network");
            snapshot.project_path = Path.Combine(root, "Second.ap17"); var second = context();
            Check(CylinderTemplateStore.PathFor(root, second) != savedPath && CylinderTemplateStore.Read(root, second) == null, "project isolation");
            second.project_path = original.project_path; second.plc_name += "2";
            Check(CylinderTemplateStore.PathFor(root, second) != savedPath, "PLC isolation");
            second.plc_name = original.plc_name; second.host_name = "..\\..\\escape";
            string escaped = CylinderTemplateStore.PathFor(root, second);
            Check(escaped.StartsWith(Path.Combine(root, "project-templates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(escaped).Length == 69, "untrusted names become hashed filenames");
            CylinderTemplateStore.Save(root, original, original.sample_fingerprint, rules, true);
            Check(File.Exists(savedPath) && !File.Exists(savedPath + ".tmp"), "atomic replacement leaves no temporary file");
            File.WriteAllText(savedPath, "{}");
            Reject(() => CylinderTemplateStore.Read(root, original), "wrong stored identity rejected");
            Console.WriteLine("PASS: cylinder project templates (" + checks + " checks), no TIA calls.");
        } finally { if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
