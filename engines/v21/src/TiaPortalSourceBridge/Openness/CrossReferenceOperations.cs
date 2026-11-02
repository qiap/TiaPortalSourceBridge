using System;
using System.Collections.Generic;
using System.Linq;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Openness
{
    internal static class CrossReferenceOperations
    {
        public static Dictionary<string, object> Query(
            OpennessSession session,
            string plcName,
            string scopeKind,
            string targetName,
            string groupPath,
            string unitName,
            string filterText,
            string symbolFilter,
            string accessFilter,
            int maxResults)
        {
            if (maxResults < 1 || maxResults > 2000) maxResults = 200;

            ReflectionApi api = session.Api;
            PlcContext plc = session.SelectPlc(plcName);
            string kind = string.IsNullOrWhiteSpace(scopeKind) ? "plc" : scopeKind.Trim().ToLowerInvariant();
            object scopeTarget = ResolveScopeTarget(session, plc, kind, targetName, groupPath, unitName);

            object xrefService = api.GetService(scopeTarget, "Siemens.Engineering.CrossReference.CrossReferenceService");
            if (xrefService == null)
            {
                throw new InvalidOperationException("目标对象不提供 Openness V21 CrossReferenceService: " + scopeTarget.GetType().Name);
            }

            string filterName = string.IsNullOrWhiteSpace(filterText) ? "ObjectsWithReferences" : filterText.Trim();
            object filterEnum = api.EnumValue("Siemens.Engineering.CrossReference.CrossReferenceFilter", filterName);

            object xrefResult;
            using (OperationMetrics.Measure("cross_reference_query"))
            {
                xrefResult = api.Invoke(xrefService, "GetCrossReferences", filterEnum);
            }

            List<object> rows = new List<object>();
            bool truncated = false;
            string symQuery = (symbolFilter ?? "").Trim();
            string accQuery = (accessFilter ?? "").Trim();

            WalkSources(api, api.Get(xrefResult, "Sources", false), symQuery, accQuery, maxResults, rows, ref truncated);

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName },
                { "unit_name", unitName ?? "" },
                { "scope_kind", kind },
                { "target_name", targetName ?? "" },
                { "cross_reference_filter", filterName },
                { "symbol_filter", symQuery },
                { "access_filter", accQuery },
                { "returned_count", rows.Count },
                { "truncated", truncated },
                { "items", rows.ToArray() }
            };
        }

        private static object ResolveScopeTarget(
            OpennessSession session,
            PlcContext plc,
            string scopeKind,
            string targetName,
            string groupPath,
            string unitName)
        {
            if (scopeKind == "plc")
            {
                return SoftwareUnitHelper.ResolveContainer(session.Api, plc, unitName);
            }
            if (string.IsNullOrWhiteSpace(targetName))
            {
                throw new ArgumentException("当 scope_kind=" + scopeKind + " 时，必须提供 target_name。");
            }
            if (scopeKind == "block")
            {
                return BlockOperations.ResolveBlockEntry(session, plc, targetName.Trim(), groupPath, unitName).Item;
            }
            if (scopeKind == "type")
            {
                return BlockOperations.ResolveTypeEntry(session, plc, targetName.Trim(), groupPath, unitName).Item;
            }
            if (scopeKind == "tag_table")
            {
                return BlockOperations.ResolveTagTableObject(session, plc, targetName.Trim(), groupPath, unitName);
            }
            throw new ArgumentException("scope_kind 仅支持 plc、block、type 或 tag_table。");
        }

        private static void WalkSources(
            ReflectionApi api,
            object sourcesComposition,
            string symQuery,
            string accQuery,
            int maxResults,
            List<object> rows,
            ref bool truncated)
        {
            foreach (object src in api.Items(sourcesComposition))
            {
                if (rows.Count >= maxResults)
                {
                    truncated = true;
                    return;
                }

                string srcName = api.GetString(src, "Name");
                string srcPath = api.GetString(src, "Path");
                string srcAddress = api.GetString(src, "Address");
                string srcType = api.GetString(src, "TypeName");

                bool srcMatchesSymbol = symQuery.Length == 0
                    || ContainsText(srcName, symQuery)
                    || ContainsText(srcPath, symQuery)
                    || ContainsText(srcAddress, symQuery);

                List<object> refEntries = new List<object>();
                foreach (object refObj in api.Items(api.Get(src, "References", false)))
                {
                    string refName = api.GetString(refObj, "Name");
                    string refPath = api.GetString(refObj, "Path");
                    string refType = api.GetString(refObj, "TypeName");

                    List<object> locEntries = new List<object>();
                    foreach (object loc in api.Items(api.Get(refObj, "Locations", false)))
                    {
                        string access = api.GetString(loc, "Access");
                        if (accQuery.Length > 0 && !ContainsText(access, accQuery))
                            continue;

                        string locName = api.GetString(loc, "Name");
                        string refLocation = api.GetString(loc, "ReferenceLocation");
                        string refAsName = api.GetString(loc, "ReferencedAsName");

                        if (!srcMatchesSymbol
                            && !ContainsText(refName, symQuery)
                            && !ContainsText(locName, symQuery)
                            && !ContainsText(refAsName, symQuery)
                            && !ContainsText(refLocation, symQuery))
                        {
                            continue;
                        }

                        locEntries.Add(new Dictionary<string, object>
                        {
                            { "name", locName },
                            { "access", access },
                            { "reference_type", api.GetString(loc, "ReferenceType") },
                            { "reference_location", refLocation },
                            { "referenced_as", api.GetString(loc, "ReferencedAs") },
                            { "referenced_as_name", refAsName },
                            { "address", api.GetString(loc, "Address") }
                        });
                    }

                    if (locEntries.Count > 0)
                    {
                        refEntries.Add(new Dictionary<string, object>
                        {
                            { "name", refName },
                            { "path", refPath },
                            { "type_name", refType },
                            { "device", api.GetString(refObj, "Device") },
                            { "locations", locEntries.ToArray() }
                        });
                    }
                }

                // For UnusedObjects / ObjectsWithoutReferences filter, emit source object even when References is empty
                if (refEntries.Count > 0 || (srcMatchesSymbol && accQuery.Length == 0 && !api.Items(api.Get(src, "References", false)).Any()))
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        { "name", srcName },
                        { "path", srcPath },
                        { "address", srcAddress },
                        { "type_name", srcType },
                        { "device", api.GetString(src, "Device") },
                        { "reference_count", refEntries.Count },
                        { "references", refEntries.ToArray() }
                    });
                }

                WalkSources(api, api.Get(src, "Children", false), symQuery, accQuery, maxResults, rows, ref truncated);
            }
        }

        private static bool ContainsText(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
