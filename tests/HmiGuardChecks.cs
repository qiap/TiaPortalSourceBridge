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
        string members = "<ObjectList><Hmi.Tag.TagStructureMember CompositionName='Members'><AttributeList><Name>State</Name><LogicalAddress>%DB1.DBX2.0</LogicalAddress></AttributeList><ObjectList><Hmi.Tag.TagStructureMember CompositionName='Members'><AttributeList><Name>Ready</Name></AttributeList></Hmi.Tag.TagStructureMember></ObjectList></Hmi.Tag.TagStructureMember></ObjectList>";
        string structure = Table("Bool").Replace("<Name>Barcode</Name>", "<Name>RFID/A.B{2}</Name>").Replace("</Hmi.Tag.Tag>", members + "</Hmi.Tag.Tag>");
        var paths = HmiValidation.TagPaths(structure).Select(p => p.Key).ToArray();
        Check(paths.Contains("RFID/A.B{2}.State.Ready") && paths.Contains("RFID/A.B{2}.State"), "nested paths preserve literal punctuation in root names");
        Check(!paths.Contains("RFID/A.B{2}.State.Readyy"), "member prefix never accepts misspelled member");
        Check(HmiValidation.Validate(structure).Count == 0, "members inherit type and need no fabricated explicit type");
        Check(HmiValidation.Compare(structure, structure.Replace(members, "")).Length > 0, "removed members fail readback");
        Check(HmiValidation.Compare(structure, structure.Replace("%DB1.DBX2.0", "%DB1.DBX3.0")).Length == 1, "member address drift detected");
        Check(HmiValidation.Validate(structure.Replace(members, members.Replace("</ObjectList></Hmi.Tag.TagStructureMember>", "</ObjectList></Hmi.Tag.TagStructureMember><Hmi.Tag.TagStructureMember><AttributeList><Name>State</Name></AttributeList></Hmi.Tag.TagStructureMember>"))).Any(e=>e.Contains("tag_path_conflict")), "ambiguous member paths rejected");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[] { new HmiDocument { kind="tag_tables", name="T", xml=structure.Replace("%DB1.DBX2.0", "%DB1.DBX3.0") } }, new Dictionary<string,string>{{"T",structure}}, issue, warnings);
        Check(issues.Contains("plc_binding_unverified"), "unchanged root binding cannot hide altered member binding");
        string internalTag = Table("Bool").Replace("<Connection><Name>Integrated1</Name></Connection>", "").Replace("<ControllerTag><Name>DB.Code</Name></ControllerTag>", "");
        Check(HmiValidation.Validate(internalTag).Count == 0, "internal symbolic-mode tag needs no fake PLC connection");
        string db = "<Document><SW.Blocks.GlobalDB><AttributeList><Name>DB</Name><Number>1</Number><MemoryLayout>Standard</MemoryLayout><Interface><Sections xmlns='urn:interface'><Section Name='Static'><Member Name='Command' Datatype='Struct'><Member Name='Recipe' Datatype='Byte'/><Member Name='Start' Datatype='Bool'/></Member><Member Name='Status' Datatype='Struct'><Member Name='NG' Datatype='Bool'/><Member Name='OK' Datatype='Bool'/><Member Name='Running' Datatype='Bool'/><Member Name='Ended' Datatype='Bool'/><Member Name='Fault' Datatype='Bool'/><Member Name='Value' Datatype='Real'/></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
        var layout = HmiPlcBindingValidation.ReadLayout(db);
        Check(layout.Bits == 64 && layout.Fields.Single(f=>f.Name=="DB.Status.Ended").Bit == 19, "standard structs pack Bool and align Real to word boundaries");
        Func<string,string,string> absolute = (type,address) => Table(type).Replace("Symbolic", "Absolute").Replace("<Name>Barcode</Name>", "<Name>Barcode</Name><LogicalAddress>"+address+"</LogicalAddress>").Replace("<ControllerTag><Name>DB.Code</Name></ControllerTag>", "");
        Func<string,string,string> verify = (type,address) => HmiPlcBindingValidation.Check(HmiValidation.Tags(absolute(type,address))[0],layout);
        Check(verify("Bool","%DB1.DBX2.3") == null, "existing Bool member accepts new HMI binding");
        Check(verify("Word","%DB1.DBW2") == null, "packed Bool alarm word accepted within declared DB");
        Check(verify("Bool","%DB1.DBX2.6") != null, "padding is not a Bool member");
        Check(verify("Real","%DB1.DBD2") != null, "wrongly aligned Real rejected");
        Check(verify("Word","%DB1.DBW8") != null, "out of bounds word rejected");
        Check(verify("Bool","%DB2.DBX2.3") != null, "wrong DB number rejected");
        Check(verify("Bool","%DB1.DBW2") != null, "type and address width mismatch rejected");
        Check(HmiPlcBindingValidation.Check(HmiValidation.Tags(absolute("Bool","%DB1.DBX2.3"))[0],HmiPlcBindingValidation.ReadLayout(db.Replace("Standard","Optimized"))) != null, "optimized DB offsets never inferred");
        Check(HmiPlcBindingValidation.Check(HmiValidation.Tags(Table("Bool","DB.Status.Ended"))[0],layout) == null, "symbolic path and type verified");
        Check(HmiPlcBindingValidation.Check(HmiValidation.Tags(Table("Int","DB.Status.Ended"))[0],layout) != null, "symbolic type mismatch rejected");
        string hiddenDb=db.Replace("<Member Name='Ended' Datatype='Bool'/>","<Member Name='Ended' Datatype='Bool'><AttributeList><BooleanAttribute Name='ExternalAccessible'>false</BooleanAttribute></AttributeList></Member>");
        Check(HmiPlcBindingValidation.Check(HmiValidation.Tags(absolute("Word","%DB1.DBW2"))[0],HmiPlcBindingValidation.ReadLayout(hiddenDb)) != null, "alarm word cannot span hidden PLC member");
        bool unsupported=false; try { HmiPlcBindingValidation.ReadLayout(db.Replace("Datatype='Real'","Datatype='UnknownUDT'")); } catch(InvalidOperationException) { unsupported=true; }
        Check(unsupported,"unknown UDT layout is blocked, never guessed");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[]{new HmiDocument{kind="tag_tables",name="T",xml=absolute("Bool","%DB1.DBX2.3")}}, existing, issue, warnings, t=>HmiPlcBindingValidation.Check(t,layout));
        Check(issues.Count==0,"verified live PLC binding can pass evidence check");
        issues.Clear();
        HmiValidation.CheckTagEvidence(new[]{new HmiDocument{kind="tag_tables",name="T",xml=absolute("Bool","%DB1.DBX2.6")}}, existing, issue, warnings, t=>HmiPlcBindingValidation.Check(t,layout));
        Check(issues.Contains("plc_binding_unverified"),"failed resolver evidence remains blocked");
        Console.WriteLine("PASS: " + count + " HMI guard checks; no project connection or writes."); return 0;
    }
}
