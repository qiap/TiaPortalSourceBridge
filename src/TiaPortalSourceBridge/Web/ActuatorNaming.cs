using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaPortalSourceBridge.Web
{
    internal static class ActuatorNaming
    {
        internal static readonly string[] Kinds={"cylinder","motor","valve","vacuum","servo"};
        internal static void Validate(string kind,Dictionary<string,object> suffixes,Dictionary<string,object> rules)
        {
            if(string.IsNullOrEmpty(kind))kind="cylinder";
            if(!Kinds.Contains(kind))throw new InvalidOperationException("请选择支持的机构类型。");
            if(suffixes==null)suffixes=new Dictionary<string,object>();
            if(kind=="cylinder"){if(suffixes.Count>0)throw new InvalidOperationException("气缸沿用已核对的伸缩命名规则，不接受覆盖后缀。");return;}
            var pins=rules.Where(p=>Equals(p.Value,"input")||Equals(p.Value,"output")).Select(p=>p.Key).ToArray();
            if(pins.Length==0||suffixes.Count!=pins.Length||pins.Any(p=>!suffixes.ContainsKey(p)))throw new InvalidOperationException("请为每个选点参数明确填写用途后缀，例如运行反馈、启动输出。");
            foreach(var suffix in suffixes.Values){var s=suffix as string;if(s==null||s!=s.Trim()||s.Length<1||s.Length>24||Regex.IsMatch(s,"[\\x00-\\x1f\\x7f\"\\\\]")||Regex.IsMatch(s,@"%?[IQEA]\s*\d+\.[0-7]$",RegexOptions.IgnoreCase))throw new InvalidOperationException("用途后缀须为 1–24 字，不含地址、双引号、反斜杠或换行。");}
        }
    }
}
