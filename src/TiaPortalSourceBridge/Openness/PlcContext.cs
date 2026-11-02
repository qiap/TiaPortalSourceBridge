using System.Collections.Generic;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class CachedBlockEntry
    {
        public string Name { get; set; }
        public string GroupPath { get; set; }
        public object Number { get; set; }
        public string Language { get; set; }
        public string Category { get; set; }
        public object Item { get; set; }
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
