using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace TiaPortalSourceBridge.Web
{
    internal sealed class ActionTarget
    {
        private readonly byte[] key = new byte[32];
        internal ActionTarget() { using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key); }
        internal string Sign(string project, string plc)
        {
            if (string.IsNullOrWhiteSpace(project) || plc == null) return "";
            using (var mac = new HMACSHA256(key))
                return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant() + "\n" + plc)));
        }
        internal bool Verify(string project, string plc, string token)
        { return !string.IsNullOrEmpty(token) && string.Equals(Sign(project, plc), token, StringComparison.Ordinal); }
    }
}
