using System;
using System.Collections.Generic;
using UIAutomationClient;

namespace TiaPortalSourceBridge.Alarms.Native
{
    // UIA3 COM facade. The legacy managed UIA client can block TIA's custom-grid provider.
    // CUIAutomation8 supplies native per-request timeouts and uses the current Windows proxy.
    internal static class Client
    {
        [ThreadStatic] private static IUIAutomation2 instance;
        [ThreadStatic] private static IUIAutomationCacheRequest cache;
        [ThreadStatic] private static IUIAutomationCacheRequest gridCache;
        internal static IUIAutomationCacheRequest GridCache{get{if(gridCache==null){gridCache=Cache.Clone();gridCache.AddProperty(30045);}return gridCache;}}
        internal static IUIAutomationCacheRequest Cache
        {
            get
            {
                if(cache==null){cache=Api.CreateCacheRequest();cache.TreeScope=(UIAutomationClient.TreeScope)1;
                    foreach(int id in new[]{30001,30003,30005,30010,30011,30022})cache.AddProperty(id);}
                return cache;
            }
        }
        internal static IUIAutomation2 Api
        {
            get
            {
                if(instance==null)
                {
                    instance=(IUIAutomation2)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E22AD333-B25F-460C-83D0-0581107395C9")));
                    instance.ConnectionTimeout=1000;instance.TransactionTimeout=2000;
                }
                return instance;
            }
        }
    }
    internal enum TreeScope { Children=2, Descendants=4 }
    internal static class ControlType
    {
        internal const int Button=50000,CheckBox=50002,Edit=50004,Image=50006,TabItem=50019,Tree=50023,DataGrid=50028,Window=50032,Table=50036;
    }
    internal class Condition
    {
        internal IUIAutomationCondition Raw;
        internal static Condition TrueCondition { get {return new Condition{Raw=Client.Api.CreateTrueCondition()};} }
    }
    internal sealed class PropertyCondition:Condition
    {internal bool Edit; internal PropertyCondition(int property,object value){Raw=Client.Api.CreatePropertyCondition(property,value);Edit=property==30003&&Convert.ToInt32(value)==50004;} }
    internal sealed class AutomationElement
    {
        private readonly IUIAutomationElement raw;
        private readonly bool hasValue;
        private readonly string caption;
        private AutomationElement(IUIAutomationElement element,bool value=false,string title=null){raw=element;hasValue=value;caption=title;}
        internal static AutomationElement FromHandle(IntPtr handle,string title=null){return new AutomationElement(Client.Api.ElementFromHandleBuildCache(handle,Client.Cache),false,title);}
        internal static AutomationElement RootElement{get{return new AutomationElement(Client.Api.GetRootElement());}}
        internal const int ControlTypeProperty=30003,ProcessIdProperty=30002,NameProperty=30005;
        internal CurrentElement Current{get{return new CurrentElement(raw,caption);}}
        internal string CachedValue{get{return hasValue?raw.GetCachedPropertyValue(30045) as string:null;}}
        internal AutomationElement FindFirst(TreeScope scope,Condition condition)
        {var found=raw.FindFirstBuildCache((UIAutomationClient.TreeScope)(int)scope,condition.Raw,Client.Cache);return found==null?null:new AutomationElement(found);}
        internal IEnumerable<AutomationElement> FindAll(TreeScope scope,Condition condition)
        {
            bool values=condition is PropertyCondition&&((PropertyCondition)condition).Edit;
            var found=raw.FindAllBuildCache((UIAutomationClient.TreeScope)(int)scope,condition.Raw,values?Client.GridCache:Client.Cache);
            var result=new List<AutomationElement>(found.Length);
            for(int i=0;i<found.Length;i++)result.Add(new AutomationElement(found.GetElement(i),values));
            return result;
        }
        internal bool TryGetCurrentPattern(int id,out object value)
        {
            value=null;object pattern;
            try{pattern=raw.GetCurrentPattern(id);}catch(System.Runtime.InteropServices.COMException ex){if((uint)ex.HResult==0x80040200)return false;throw;}
            if(pattern==null)return false;
            switch(id)
            {
                case 10000:value=new InvokePattern((IUIAutomationInvokePattern)pattern);break;
                case 10002:value=new ValuePattern((IUIAutomationValuePattern)pattern);break;
                case 10009:value=new WindowPattern((IUIAutomationWindowPattern)pattern);break;
                case 10010:value=new SelectionItemPattern((IUIAutomationSelectionItemPattern)pattern);break;
                case 10015:value=new TogglePattern((IUIAutomationTogglePattern)pattern);break;
            }
            return value!=null;
        }
    }
    internal sealed class CurrentElement
    {
        private readonly IUIAutomationElement e;
        private readonly string caption;
        internal CurrentElement(IUIAutomationElement value,string title=null){e=value;caption=title;}
        internal string Name{get{return caption??e.CachedName;}}
        internal string AutomationId{get{return e.CachedAutomationId;}}
        internal int ControlType{get{return e.CachedControlType;}}
        internal bool IsEnabled{get{return e.CachedIsEnabled!=0;}}
        internal bool IsOffscreen{get{return e.CachedIsOffscreen!=0;}}
        internal Bounds BoundingRectangle{get{var r=e.CachedBoundingRectangle;return new Bounds{Left=r.left,Top=r.top,Width=r.right-r.left,Height=r.bottom-r.top};}}
    }
    internal struct Bounds{internal double Left,Top,Width,Height;internal bool IsEmpty{get{return Width<=0||Height<=0;}}}
    internal sealed class InvokePattern
    {internal const int Pattern=10000;private readonly IUIAutomationInvokePattern p;internal InvokePattern(IUIAutomationInvokePattern value){p=value;}internal void Invoke(){p.Invoke();}}
    internal sealed class SelectionItemPattern
    {internal const int Pattern=10010;private readonly IUIAutomationSelectionItemPattern p;internal SelectionItemPattern(IUIAutomationSelectionItemPattern value){p=value;}internal bool IsSelected{get{return p.CurrentIsSelected!=0;}}internal void Select(){p.Select();}}
    internal sealed class ValuePattern
    {internal const int Pattern=10002;private readonly IUIAutomationValuePattern p;internal ValuePattern(IUIAutomationValuePattern value){p=value;}internal ValuePattern Current{get{return this;}}internal string Value{get{return p.CurrentValue;}}internal void SetValue(string text){p.SetValue(text);}}
    internal enum ToggleState{Off=0,On=1,Indeterminate=2}
    internal sealed class TogglePattern
    {internal const int Pattern=10015;private readonly IUIAutomationTogglePattern p;internal TogglePattern(IUIAutomationTogglePattern value){p=value;}internal TogglePattern Current{get{return this;}}internal ToggleState ToggleState{get{return (ToggleState)(int)p.CurrentToggleState;}}internal void Toggle(){p.Toggle();}}
    internal enum WindowVisualState{Normal=0,Maximized=1,Minimized=2}
    internal sealed class WindowPattern
    {internal const int Pattern=10009;private readonly IUIAutomationWindowPattern p;internal WindowPattern(IUIAutomationWindowPattern value){p=value;}internal WindowPattern Current{get{return this;}}internal WindowVisualState WindowVisualState{get{return (WindowVisualState)(int)p.CurrentWindowVisualState;}}internal void SetWindowVisualState(WindowVisualState state){p.SetWindowVisualState((UIAutomationClient.WindowVisualState)(int)state);}}
}
