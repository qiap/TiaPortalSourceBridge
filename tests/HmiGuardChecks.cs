using System;
using System.Linq;
using System.Collections.Generic;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

internal static class HmiGuardChecks
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
    static string Table(string type, string symbol = "DB.Code")
    {
        return "<Document><Engineering version='V17'/><Hmi.Tag.TagTable ID='0'><AttributeList><Name>T</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID='1'><AttributeList><Name>Barcode</Name><Length>48</Length><AddressAccessMode>Symbolic</AddressAccessMode></AttributeList><LinkList><DataType><Name>" + type + "</Name></DataType><HmiDataType><Name>" + type + "</Name></HmiDataType><Connection><Name>Integrated1</Name></Connection><ControllerTag><Name>" + symbol + "</Name></ControllerTag></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>";
    }
    static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, name); }
    static int Main(string[] args)
    {
        string expected = Table("String");
        Check(HmiValidation.Validate(expected).Count == 0, "explicit typed XML structurally valid");
        Check(HmiValidation.Compare(expected, Table("Int")).Length == 2, "compile-success/default-Int regression detected");
        Check(HmiValidation.Compare(Table("UDInt"), Table("Int")).Length == 2, "UDInt coercion detected");
        Check(HmiValidation.Compare(Table("Bool"), Table("Int")).Length == 2, "Bool coercion detected");
        Check(HmiValidation.Compare(expected, Table("String", "DB.Other")).Length == 1, "PLC symbol change detected");
        Check(HmiValidation.Compare(expected, expected.Replace("<Name>Integrated1</Name>", "<Name>Wrong</Name>")).Length == 1, "connection change detected");
        Check(HmiValidation.Compare(expected, expected.Replace("<Length>48</Length>", "<Length>2</Length>")).Length == 1, "string truncation detected");
        Check(HmiValidation.Compare(expected, expected.Replace("ID='1'", "ID='FA'").Replace("><", ">\n  <")).Length == 0, "renumbering and indentation ignored");
        Check(HmiValidation.Validate(expected.Replace("<DataType><Name>String</Name></DataType>", "")).Any(e => e.Contains("explicit_type_required")), "missing type rejected");
        Check(HmiValidation.Validate(expected.Replace("ControllerTag", "PlcTag")).Any(e => e.Contains("invalid_symbol_link")), "wrong PLC link rejected");
        Check(HmiValidation.Validate(expected.Replace("<Length>48</Length>", "")).Any(e => e.Contains("string_length_required")), "missing string length rejected");
        string screen = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name></AttributeList><ObjectList><Hmi.Screen.IOField><AttributeList><ObjectName>BarcodeField</ObjectName></AttributeList><ObjectList><Hmi.Dynamic.TagConnectionDynamic><LinkList><Tag><Name>Barcode</Name></Tag></LinkList></Hmi.Dynamic.TagConnectionDynamic></ObjectList></Hmi.Screen.IOField></ObjectList></Hmi.Screen.Screen></Document>";
        Check(HmiValidation.Compare(screen, screen.Replace("<Name>Barcode</Name>", "<Name>Wrong</Name>")).Length == 1, "screen binding change detected");
        Check(HmiValidation.Compare(screen, screen.Replace("<Tag><Name>Barcode</Name></Tag>", "")).Length == 1, "screen binding loss detected");
        string model = "{\"tag_tables\":[{\"name\":\"T\",\"tags\":[{\"name\":\"B\",\"data_type\":\"Bool\",\"connection\":\"C\",\"plc_tag\":\"DB.B\"}]}]}";
        var generated = HmiModelBuilder.Generate(model, "V17", args[0]);
        Check(generated[0].xml.Contains("ControllerTag") && !generated[0].xml.Contains("<PlcTag"), "generator emits native symbol link");
        Reject(() => HmiModelBuilder.Generate(model.Replace("\"data_type\":\"Bool\",", ""), "V17", args[0]), "generator requires explicit type");
        Reject(() => HmiModelBuilder.Generate(model.Replace("Bool", "String"), "V17", args[0]), "generator requires explicit string length");
        string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new[] { new { kind = "tag_tables", xml = expected }, new { kind = "tag_tables", xml = expected } });
        Reject(() => HmiOperations.ParseDocuments(json, "V17"), "whole batch duplicate identity rejected");
        Check(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(HmiOperations.Coverage()).Contains("integrated_connections"), "coverage explicitly excludes integrated connections");
        var issues = new List<string>(); var warnings = new List<string>();
        var existing = new Dictionary<string, string> { { "T", Table("Bool") } };
        Action<string, string, string> issue = (code, name, detail) => issues.Add(code);
        HmiValidation.CheckTagEvidence(new[] { new HmiDocument { kind = "tag_tables", name = "T", xml = expected } }, existing, issue, warnings);
        Check(issues.Contains("type_unverified"), "unwitnessed String type blocked before import");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[] { new HmiDocument { kind = "tag_tables", name = "T", xml = Table("Bool", "DB.New") } }, existing, issue, warnings);
        Check(issues.Contains("plc_binding_unverified"), "new PLC binding blocked before import");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[] { new HmiDocument { kind = "tag_tables", name = "T2", xml = Table("Bool") } }, existing, issue, warnings);
        Check(issues.Contains("tag_name_conflict"), "cross-table name conflict blocked before import");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[] { new HmiDocument { kind = "tag_tables", name = "T", xml = Table("Bool") } }, existing, issue, warnings);
        Check(issues.Count == 0 && warnings.Count > 0, "preserved native binding reports PLC-end validation limitation");
        Console.WriteLine("PASS: " + count + " HMI guard checks; no project connection or writes."); return 0;
    }
}
