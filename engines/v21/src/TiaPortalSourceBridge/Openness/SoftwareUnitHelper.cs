using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaPortalSourceBridge.Openness
{
    internal static class SoftwareUnitHelper
    {
        public static IList<object> GetUnits(ReflectionApi api, PlcContext plc)
        {
            List<object> list = new List<object>();
            object provider = api.GetService(plc.Software, "Siemens.Engineering.SW.Units.PlcUnitProvider");
            if (provider == null) return list;
            object unitGroup = api.Get(provider, "UnitGroup", false);
            if (unitGroup == null) return list;

            foreach (object unit in api.Items(api.Get(unitGroup, "Units", false)))
            {
                list.Add(unit);
            }
            foreach (object safetyUnit in api.Items(api.Get(unitGroup, "SafetyUnits", false)))
            {
                list.Add(safetyUnit);
            }
            return list;
        }

        public static object ResolveUnit(ReflectionApi api, PlcContext plc, string unitName)
        {
            if (string.IsNullOrWhiteSpace(unitName)) return null;
            string trimmed = unitName.Trim();
            IList<object> units = GetUnits(api, plc);
            object matched = units.FirstOrDefault(u =>
                string.Equals(api.GetString(u, "Name"), trimmed, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;

            string available = units.Count == 0
                ? "<当前 PLC 未配置 Software Units>"
                : string.Join(", ", units.Select(u => api.GetString(u, "Name")));
            throw new InvalidOperationException("找不到 Software Unit: " + trimmed + "。可用单元: " + available);
        }

        public static object ResolveContainer(ReflectionApi api, PlcContext plc, string unitName)
        {
            return string.IsNullOrWhiteSpace(unitName) ? plc.Software : ResolveUnit(api, plc, unitName);
        }

        public static CachedBlockEntry BuildEntry(ReflectionApi api, object item, string groupPath, string unitName, bool isType)
        {
            string name = api.GetString(item, "Name");
            string typeName = item.GetType().Name;
            string language = isType ? ResolveTypeCategory(typeName) : api.GetString(item, "ProgrammingLanguage");
            string category = isType ? language : ResolveBlockCategory(typeName);
            object number = isType ? null : api.Get(item, "Number", false);
            string ns = api.GetString(item, "Namespace");

            object consistentObj = api.Get(item, "IsConsistent", false);
            bool? isConsistent = consistentObj is bool ? (bool?)((bool)consistentObj) : null;

            object khpObj = api.Get(item, "IsKnowHowProtected", false);
            bool? isKhp = khpObj is bool ? (bool?)((bool)khpObj) : null;

            return new CachedBlockEntry
            {
                Name = name,
                GroupPath = groupPath ?? "",
                UnitName = unitName ?? "",
                Namespace = ns ?? "",
                Number = number,
                Language = language,
                Category = category,
                IsConsistent = isConsistent,
                IsKnowHowProtected = isKhp,
                ModifiedDate = FormatDate(api.Get(item, "ModifiedDate", false)),
                CodeModifiedDate = FormatDate(api.Get(item, "CodeModifiedDate", false)),
                InterfaceModifiedDate = FormatDate(api.Get(item, "InterfaceModifiedDate", false)),
                Item = item
            };
        }

        public static Dictionary<string, object> ListSoftwareUnits(OpennessSession session, string plcName)
        {
            ReflectionApi api = session.Api;
            PlcContext plc = session.SelectPlc(plcName);
            IList<object> units = GetUnits(api, plc);
            List<object> rows = new List<object>();

            foreach (object unit in units)
            {
                string name = api.GetString(unit, "Name");
                string nsPreset = api.GetString(unit, "NamespacePreset");
                string author = api.GetString(unit, "Author");
                int blockCount = CountGroupItems(api, api.Get(unit, "BlockGroup", false), "Blocks");
                int typeCount = CountGroupItems(api, api.Get(unit, "TypeGroup", false), "Types");
                int tagTableCount = CountGroupItems(api, api.Get(unit, "TagTableGroup", false), "TagTables");

                List<object> relations = new List<object>();
                foreach (object rel in api.Items(api.Get(unit, "Relations", false)))
                {
                    object related = api.Get(rel, "RelatedObject", false);
                    relations.Add(new Dictionary<string, object>
                    {
                        { "relation_type", api.GetString(rel, "RelationType") },
                        { "related_object", related == null ? "" : api.GetString(related, "Name") }
                    });
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "name", name },
                    { "unit_type", unit.GetType().Name },
                    { "namespace_preset", nsPreset },
                    { "author", author },
                    { "block_count", blockCount },
                    { "type_count", typeCount },
                    { "tag_table_count", tagTableCount },
                    { "relations", relations.ToArray() }
                });
            }

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName },
                { "unit_count", rows.Count },
                { "units", rows.ToArray() }
            };
        }

        private static int CountGroupItems(ReflectionApi api, object group, string itemsProperty)
        {
            if (group == null) return 0;
            int total = api.Items(api.Get(group, itemsProperty, false)).Count();
            foreach (object child in api.Items(api.Get(group, "Groups", false)))
            {
                total += CountGroupItems(api, child, itemsProperty);
            }
            return total;
        }

        private static string ResolveTypeCategory(string clrTypeName)
        {
            if (clrTypeName.IndexOf("NamedValue", StringComparison.OrdinalIgnoreCase) >= 0) return "NVT";
            if (clrTypeName.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0) return "SDT";
            return "UDT";
        }

        private static string ResolveBlockCategory(string clrTypeName)
        {
            if (clrTypeName.IndexOf("OB", StringComparison.OrdinalIgnoreCase) >= 0) return "OB";
            if (clrTypeName.IndexOf("FB", StringComparison.OrdinalIgnoreCase) >= 0) return "FB";
            if (clrTypeName.IndexOf("FC", StringComparison.OrdinalIgnoreCase) >= 0) return "FC";
            if (clrTypeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0 || clrTypeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0) return "DB";
            return clrTypeName;
        }

        private static string FormatDate(object raw)
        {
            if (raw == null) return "";
            if (raw is DateTime)
            {
                DateTime dt = (DateTime)raw;
                if (dt == DateTime.MinValue) return "";
                return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }
            return Convert.ToString(raw);
        }
    }
}
