using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Openness
{
    internal static class UnifiedHmiOperations
    {
        public static Dictionary<string, object> Read(
            OpennessSession session,
            string hmiName,
            string kind,
            string nameFilter,
            int limit)
        {
            if (limit < 1 || limit > 1000) limit = 200;
            ReflectionApi api = session.Api;
            UnifiedHmiContext hmi = session.SelectUnifiedHmi(hmiName);
            object sw = hmi.Software;
            string k = string.IsNullOrWhiteSpace(kind) ? "summary" : kind.Trim().ToLowerInvariant();
            string filter = (nameFilter ?? "").Trim();

            if (k == "summary")
            {
                return new Dictionary<string, object>
                {
                    { "hmi", hmi.DisplayName },
                    { "software_type", sw.GetType().FullName },
                    { "screen_count", CountScreens(api, sw) },
                    { "tag_count", api.Items(api.Get(sw, "Tags", false)).Count() },
                    { "tag_table_count", CountTagTables(api, sw) },
                    { "discrete_alarm_count", api.Items(api.Get(sw, "DiscreteAlarms", false)).Count() },
                    { "analog_alarm_count", api.Items(api.Get(sw, "AnalogAlarms", false)).Count() },
                    { "alarm_class_count", api.Items(api.Get(sw, "AlarmClasses", false)).Count() },
                    { "connection_count", api.Items(api.Get(sw, "Connections", false)).Count() },
                    { "script_count", api.Items(api.Get(sw, "Scripts", false)).Count() },
                    { "text_list_count", api.Items(api.Get(sw, "HmiTextLists", false)).Count() }
                };
            }

            List<object> items = new List<object>();
            bool truncated = false;

            if (k == "screens")
            {
                CollectScreens(api, api.Get(sw, "Screens", false), "", filter, limit, items, ref truncated);
                CollectScreenGroups(api, api.Get(sw, "ScreenGroups", false), "", filter, limit, items, ref truncated);
            }
            else if (k == "tags")
            {
                foreach (object tag in api.Items(api.Get(sw, "Tags", false)))
                {
                    string name = api.GetString(tag, "Name");
                    if (!Matches(name, filter) && !Matches(api.GetString(tag, "PlcTag"), filter)) continue;
                    if (items.Count >= limit) { truncated = true; break; }
                    items.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "tag_table", api.GetString(tag, "TagTableName") },
                        { "data_type", api.GetString(tag, "DataType") },
                        { "hmi_data_type", api.GetString(tag, "HmiDataType") },
                        { "connection", api.GetString(tag, "Connection") },
                        { "plc_name", api.GetString(tag, "PlcName") },
                        { "plc_tag", api.GetString(tag, "PlcTag") },
                        { "address", api.GetString(tag, "Address") },
                        { "acquisition_cycle", api.GetString(tag, "AcquisitionCycle") }
                    });
                }
            }
            else if (k == "discrete_alarms")
            {
                foreach (object alarm in api.Items(api.Get(sw, "DiscreteAlarms", false)))
                {
                    string name = api.GetString(alarm, "Name");
                    string eventText = ReadMultilingualText(api, api.Get(alarm, "EventText", false));
                    string triggerTag = api.GetString(alarm, "RaisedStateTag");
                    if (!Matches(name, filter) && !Matches(eventText, filter) && !Matches(triggerTag, filter)) continue;
                    if (items.Count >= limit) { truncated = true; break; }
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", api.Get(alarm, "Id", false) },
                        { "name", name },
                        { "alarm_class", api.GetString(alarm, "AlarmClass") },
                        { "priority", api.Get(alarm, "Priority", false) },
                        { "raised_state_tag", triggerTag },
                        { "raised_state_bit", api.Get(alarm, "RaisedStateTagBitNumber", false) },
                        { "trigger_bit_address", api.GetString(alarm, "TriggerBitAddress") },
                        { "trigger_mode", api.GetString(alarm, "TriggerMode") },
                        { "ack_control_tag", api.GetString(alarm, "AcknowledgmentControlTag") },
                        { "ack_control_bit", api.Get(alarm, "AcknowledgmentControlTagBitNumber", false) },
                        { "area", api.GetString(alarm, "Area") },
                        { "origin", api.GetString(alarm, "Origin") },
                        { "event_text", eventText },
                        { "info_text", ReadMultilingualText(api, api.Get(alarm, "InfoText", false)) }
                    });
                }
            }
            else if (k == "analog_alarms")
            {
                foreach (object alarm in api.Items(api.Get(sw, "AnalogAlarms", false)))
                {
                    string name = api.GetString(alarm, "Name");
                    string eventText = ReadMultilingualText(api, api.Get(alarm, "EventText", false));
                    if (!Matches(name, filter) && !Matches(eventText, filter)) continue;
                    if (items.Count >= limit) { truncated = true; break; }
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", api.Get(alarm, "Id", false) },
                        { "name", name },
                        { "alarm_class", api.GetString(alarm, "AlarmClass") },
                        { "priority", api.Get(alarm, "Priority", false) },
                        { "raised_state_tag", api.GetString(alarm, "RaisedStateTag") },
                        { "condition", api.GetString(alarm, "Condition") },
                        { "condition_value", api.Get(alarm, "ConditionValue", false) },
                        { "trigger_address", api.GetString(alarm, "TriggerAddress") },
                        { "event_text", eventText }
                    });
                }
            }
            else if (k == "alarm_classes")
            {
                foreach (object cls in api.Items(api.Get(sw, "AlarmClasses", false)))
                {
                    string name = api.GetString(cls, "Name");
                    if (!Matches(name, filter)) continue;
                    if (items.Count >= limit) { truncated = true; break; }
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", api.Get(cls, "Id", false) },
                        { "name", name },
                        { "priority", api.Get(cls, "Priority", false) },
                        { "state_machine", api.GetString(cls, "StateMachine") },
                        { "log", api.GetString(cls, "Log") },
                        { "is_system", api.Get(cls, "IsSystem", false) }
                    });
                }
            }
            else if (k == "connections" || k == "scripts" || k == "text_lists" || k == "tag_tables")
            {
                string prop = k == "connections" ? "Connections" : k == "scripts" ? "Scripts" : k == "text_lists" ? "HmiTextLists" : "TagTables";
                foreach (object obj in api.Items(api.Get(sw, prop, false)))
                {
                    string name = api.GetString(obj, "Name");
                    if (!Matches(name, filter)) continue;
                    if (items.Count >= limit) { truncated = true; break; }
                    items.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "type", obj.GetType().Name }
                    });
                }
            }
            else
            {
                throw new ArgumentException("Unified HMI kind 支持: summary, screens, tags, tag_tables, discrete_alarms, analog_alarms, alarm_classes, connections, scripts, text_lists。");
            }

            return new Dictionary<string, object>
            {
                { "hmi", hmi.DisplayName },
                { "kind", k },
                { "returned_count", items.Count },
                { "truncated", truncated },
                { "items", items.ToArray() }
            };
        }

        private static int CountScreens(ReflectionApi api, object sw)
        {
            int count = api.Items(api.Get(sw, "Screens", false)).Count();
            foreach (object g in api.Items(api.Get(sw, "ScreenGroups", false)))
                count += CountScreensInGroup(api, g);
            return count;
        }

        private static int CountScreensInGroup(ReflectionApi api, object group)
        {
            int count = api.Items(api.Get(group, "Screens", false)).Count();
            foreach (object child in api.Items(api.Get(group, "Groups", false)))
                count += CountScreensInGroup(api, child);
            return count;
        }

        private static int CountTagTables(ReflectionApi api, object sw)
        {
            int count = api.Items(api.Get(sw, "TagTables", false)).Count();
            foreach (object g in api.Items(api.Get(sw, "TagTableGroups", false)))
                count += CountTagTablesInGroup(api, g);
            return count;
        }

        private static int CountTagTablesInGroup(ReflectionApi api, object group)
        {
            int count = api.Items(api.Get(group, "TagTables", false)).Count();
            foreach (object child in api.Items(api.Get(group, "Groups", false)))
                count += CountTagTablesInGroup(api, child);
            return count;
        }

        private static void CollectScreens(ReflectionApi api, object screens, string groupPath, string filter, int limit, List<object> items, ref bool truncated)
        {
            foreach (object s in api.Items(screens))
            {
                string name = api.GetString(s, "Name");
                if (!Matches(name, filter)) continue;
                if (items.Count >= limit) { truncated = true; return; }
                items.Add(new Dictionary<string, object>
                {
                    { "name", name },
                    { "group_path", groupPath },
                    { "screen_number", api.Get(s, "ScreenNumber", false) },
                    { "width", api.Get(s, "Width", false) },
                    { "height", api.Get(s, "Height", false) },
                    { "item_count", api.Items(api.Get(s, "ScreenItems", false)).Count() }
                });
            }
        }

        private static void CollectScreenGroups(ReflectionApi api, object groups, string prefix, string filter, int limit, List<object> items, ref bool truncated)
        {
            foreach (object g in api.Items(groups))
            {
                if (truncated) return;
                string gName = api.GetString(g, "Name");
                string path = prefix.Length == 0 ? gName : prefix + "/" + gName;
                CollectScreens(api, api.Get(g, "Screens", false), path, filter, limit, items, ref truncated);
                CollectScreenGroups(api, api.Get(g, "Groups", false), path, filter, limit, items, ref truncated);
            }
        }

        private static string ReadMultilingualText(ReflectionApi api, object mlText)
        {
            if (mlText == null) return "";
            List<string> parts = new List<string>();
            foreach (object item in api.Items(api.Get(mlText, "Items", false)))
            {
                string text = api.GetString(item, "Text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                object lang = api.Get(item, "Language", false);
                string culture = lang == null ? "" : api.GetString(lang, "Culture");
                parts.Add(string.IsNullOrEmpty(culture) ? text : "[" + culture + "] " + text);
            }
            return string.Join(" | ", parts);
        }

        private static bool Matches(string value, string filter)
        {
            return string.IsNullOrEmpty(filter)
                || (!string.IsNullOrEmpty(value) && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal static class HardwareCatalogOperations
    {
        public static Dictionary<string, object> Search(
            OpennessSession session,
            string filter,
            string catalogPathFilter,
            int limit)
        {
            if (string.IsNullOrWhiteSpace(filter))
                throw new ArgumentException("filter 不能为空（例如 'CPU 1516'、'6ES7 515'、'1214C'）。");
            if (limit < 1 || limit > 500) limit = 50;

            ReflectionApi api = session.Api;
            object catalog = api.Get(session.Portal, "HardwareCatalog", true);
            object rawEntries;
            using (OperationMetrics.Measure("hardware_catalog_find"))
            {
                rawEntries = string.IsNullOrWhiteSpace(catalogPathFilter)
                    ? api.Invoke(catalog, "Find", filter.Trim())
                    : api.Invoke(catalog, "Find", filter.Trim(), catalogPathFilter.Trim());
            }

            List<object> rows = new List<object>();
            bool truncated = false;
            foreach (object entry in api.Items(rawEntries))
            {
                if (rows.Count >= limit) { truncated = true; break; }
                rows.Add(new Dictionary<string, object>
                {
                    { "type_identifier", api.GetString(entry, "TypeIdentifier") },
                    { "type_identifier_normalized", api.GetString(entry, "TypeIdentifierNormalized") },
                    { "article_number", api.GetString(entry, "ArticleNumber") },
                    { "version", api.GetString(entry, "Version") },
                    { "type_name", api.GetString(entry, "TypeName") },
                    { "catalog_path", api.GetString(entry, "CatalogPath") },
                    { "description", api.GetString(entry, "Description") }
                });
            }

            return new Dictionary<string, object>
            {
                { "filter", filter.Trim() },
                { "catalog_path_filter", catalogPathFilter ?? "" },
                { "returned_count", rows.Count },
                { "truncated", truncated },
                { "entries", rows.ToArray() }
            };
        }
    }

    internal static class CaxOperations
    {
        public static Dictionary<string, object> ExportAml(
            OpennessSession session,
            string outputPath,
            string deviceName)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("aml_path 不能为空。");

            ReflectionApi api = session.Api;
            object cax = api.GetService(session.Project, "Siemens.Engineering.Cax.CaxProvider");
            if (cax == null)
                throw new InvalidOperationException("当前 TIA Portal V21 项目未提供 CaxProvider（AutomationML）服务。");

            string fullAml = Path.GetFullPath(outputPath);
            if (!fullAml.EndsWith(".aml", StringComparison.OrdinalIgnoreCase))
                fullAml += ".aml";
            Directory.CreateDirectory(Path.GetDirectoryName(fullAml));
            if (File.Exists(fullAml)) File.Delete(fullAml);

            string logPath = fullAml + ".log";
            if (File.Exists(logPath)) File.Delete(logPath);

            object target = session.Project;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                object devices = api.Get(session.Project, "Devices", true);
                object matched = api.Items(devices).FirstOrDefault(d =>
                    string.Equals(api.GetString(d, "Name"), deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (matched == null)
                    throw new InvalidOperationException("找不到指定设备用于 AML 导出: " + deviceName);
                target = matched;
            }

            object succeeded = api.Invoke(cax, "Export", target, new FileInfo(fullAml), new FileInfo(logPath));
            string logText = File.Exists(logPath) ? File.ReadAllText(logPath) : "";

            return new Dictionary<string, object>
            {
                { "aml_path", fullAml },
                { "log_path", logPath },
                { "device_name", deviceName ?? "" },
                { "succeeded", Convert.ToBoolean(succeeded ?? true) },
                { "log", logText.Length > 4000 ? logText.Substring(0, 4000) : logText }
            };
        }

        public static Dictionary<string, object> ImportAml(
            OpennessSession session,
            string amlPath,
            string importOptionText)
        {
            if (string.IsNullOrWhiteSpace(amlPath))
                throw new ArgumentException("aml_path 不能为空。");
            string fullAml = Path.GetFullPath(amlPath);
            if (!File.Exists(fullAml))
                throw new FileNotFoundException("找不到 AML 文件。", fullAml);

            ReflectionApi api = session.Api;
            object cax = api.GetService(session.Project, "Siemens.Engineering.Cax.CaxProvider");
            if (cax == null)
                throw new InvalidOperationException("当前 TIA Portal V21 项目未提供 CaxProvider（AutomationML）服务。");

            string optName = string.IsNullOrWhiteSpace(importOptionText) ? "RetainTiaDevice" : importOptionText.Trim();
            object optEnum = api.EnumValue("Siemens.Engineering.Cax.CaxImportOptions", optName);
            string logPath = Path.Combine(Path.GetTempPath(), "TiaV21_CaxImport_" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                object succeeded = session.RunInTransaction("Import AutomationML " + Path.GetFileName(fullAml), true, delegate
                {
                    return api.Invoke(cax, "Import", new FileInfo(fullAml), new FileInfo(logPath), optEnum);
                });
                session.RefreshSoftware();
                string logText = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
                return new Dictionary<string, object>
                {
                    { "aml_path", fullAml },
                    { "import_option", optName },
                    { "succeeded", Convert.ToBoolean(succeeded ?? true) },
                    { "log", logText.Length > 4000 ? logText.Substring(0, 4000) : logText },
                    { "saved", false }
                };
            }
            finally
            {
                try { if (File.Exists(logPath)) File.Delete(logPath); }
                catch { }
            }
        }
    }
}
