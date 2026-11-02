using System.Collections.Generic;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class CachedBlockEntry
    {
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public string UnitName { get; set; }
        public string Namespace { get; set; }
        public object Number { get; set; }
        public string Language { get; set; }
        public string Category { get; set; }
        public bool? IsConsistent { get; set; }
        public bool? IsKnowHowProtected { get; set; }
        public string ModifiedDate { get; set; }
        public string CodeModifiedDate { get; set; }
        public string InterfaceModifiedDate { get; set; }
        public object Item { get; set; }

        public CachedBlockEntry()
        {
            UnitName = "";
            Namespace = "";
            ModifiedDate = "";
            CodeModifiedDate = "";
            InterfaceModifiedDate = "";
        }
    }

    internal sealed class UnifiedHmiContext
    {
        public object Software { get; private set; }
        public object DeviceItem { get; private set; }
        public string DisplayName { get; private set; }
        public IList<string> MatchNames { get; private set; }

        public UnifiedHmiContext(object software, string displayName, IList<string> matchNames, object deviceItem = null)
        {
            Software = software;
            DeviceItem = deviceItem;
            DisplayName = displayName;
            MatchNames = matchNames;
        }
    }

    internal sealed class PlcContext
    {
        public object Software { get; private set; }
        public object DeviceItem { get; private set; }
        public string DisplayName { get; private set; }
        public IList<string> MatchNames { get; private set; }
        public List<CachedBlockEntry> BlockCache { get; set; }
        public List<CachedBlockEntry> TypeCache { get; set; }

        public PlcContext(object software, string displayName, IList<string> matchNames, object deviceItem = null)
        {
            Software = software;
            DeviceItem = deviceItem;
            DisplayName = displayName;
            MatchNames = matchNames;
        }

        public void InvalidateBlockCache()
        {
            BlockCache = null;
        }

        public void InvalidateTypeCache()
        {
            TypeCache = null;
        }
    }
}
