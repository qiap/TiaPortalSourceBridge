using System.Collections.Generic;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class HmiContext
    {
        public object Target { get; private set; }
        public string DisplayName { get; private set; }
        public IList<string> MatchNames { get; private set; }

        public HmiContext(object target, string displayName, IList<string> matchNames)
        {
            Target = target;
            DisplayName = displayName;
            MatchNames = matchNames;
        }
    }
}
