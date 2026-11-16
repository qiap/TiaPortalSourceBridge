using System;
using System.Net;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private static bool IsDashboardOrigin(HttpListenerRequest request)
        {
            if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
            string text = request.Headers["Origin"];
            if (string.IsNullOrEmpty(text)) return true;
            Uri origin, authority;
            // HTTP.sys uses the receiving port in Url. An SSH tunnel preserves the
            // browser's Host header, including its different local forwarded port.
            // Trust no proxy headers; require the same loopback host as the listener.
            if (!Uri.TryCreate(request.Url.Scheme + "://" + request.Headers["Host"], UriKind.Absolute, out authority) ||
                !authority.IsLoopback || authority.Host != request.Url.Host || authority.UserInfo.Length != 0 ||
                authority.AbsolutePath != "/" || authority.Query.Length != 0 || authority.Fragment.Length != 0)
                return false;
            return Uri.TryCreate(text, UriKind.Absolute, out origin) && origin.UserInfo.Length == 0 &&
                origin.Scheme == authority.Scheme && origin.Host == authority.Host && origin.Port == authority.Port;
        }
    }
}
