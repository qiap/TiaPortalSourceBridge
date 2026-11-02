using System;
using System.IO;
using System.Text;

namespace TiaPortalSourceBridge.Web
{
    internal static class EmbeddedAssets
    {
        public static string GetDashboardHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
  <meta charset=""UTF-8"">
  <title>TIA Portal Source Bridge - 控制看板</title>
  <style>
    body { background: #0b1120; color: #f1f5f9; font-family: sans-serif; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; }
    .card { background: #1e293b; border: 1px solid #334155; padding: 32px; border-radius: 12px; max-width: 500px; text-align: center; }
    h2 { color: #00a3a6; margin-bottom: 12px; }
    p { color: #94a3b8; line-height: 1.6; }
    .code { font-family: monospace; background: #0f172a; padding: 4px 8px; border-radius: 4px; color: #38bdf8; }
  </style>
</head>
<body>
  <div class=""card"">
    <h2>TIA Portal Source Bridge</h2>
    <p>可视化 Web 控制看板正在运行。</p>
    <p>请确保 <span class=""code"">web/index.html</span> 位于工具根目录下以加载完整控制界面。</p>
  </div>
</body>
</html>";
        }
    }
}
