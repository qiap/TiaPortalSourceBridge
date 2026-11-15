using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class LocalPipeHelper
    {
        private const uint PipeAccessDuplex = 0x00000003;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint PipeTypeByte = 0x00000000;
        private const uint PipeReadmodeByte = 0x00000000;
        private const uint PipeWait = 0x00000000;
        private const uint PipeRejectRemoteClients = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipeW(
            string lpName,
            uint dwOpenMode,
            uint dwPipeMode,
            uint nMaxInstances,
            uint nOutBufferSize,
            uint nInBufferSize,
            uint nDefaultTimeOut,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes);

        /// <summary>
        /// Creates a local-only named pipe owned by the current Windows user.
        /// Uses PIPE_REJECT_REMOTE_CLIENTS to block remote SMB clients without denying
        /// NetworkSid, so same-user local SSH sessions (e.g. mcp-client over ssh) can connect.
        /// </summary>
        internal static NamedPipeServerStream CreateLocalServerPipe(string pipeName, int bufferSize, bool asynchronous)
        {
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl, AccessControlType.Allow));

            byte[] sdBytes = security.GetSecurityDescriptorBinaryForm();
            GCHandle pinned = GCHandle.Alloc(sdBytes, GCHandleType.Pinned);
            SafePipeHandle handle = null;
            try
            {
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                sa.lpSecurityDescriptor = pinned.AddrOfPinnedObject();
                sa.bInheritHandle = false;

                uint openMode = PipeAccessDuplex | (asynchronous ? FileFlagOverlapped : 0u);
                uint pipeMode = PipeTypeByte | PipeReadmodeByte | PipeWait | PipeRejectRemoteClients;
                handle = CreateNamedPipeW(
                    "\\\\.\\pipe\\" + pipeName,
                    openMode,
                    pipeMode,
                    1,
                    (uint)bufferSize,
                    (uint)bufferSize,
                    0,
                    ref sa);
                int error = Marshal.GetLastWin32Error();
                if (handle == null || handle.IsInvalid)
                {
                    if (handle != null) handle.Dispose();
                    throw new Win32Exception(error);
                }
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }

            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, asynchronous, false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }
}
