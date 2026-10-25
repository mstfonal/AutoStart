using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoStart
{
    internal static class TargetProcesses
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        private static readonly int SessionId = Process.GetCurrentProcess().SessionId;

        internal static bool SamePath(string? actual, string? expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
            try { return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static bool Matches(int pid, string target)
        {
            if (pid <= 0 || !ProcessIdToSessionId((uint)pid, out uint session) || session != SessionId) return false;
            IntPtr handle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION, no mutation privileges
            if (handle == IntPtr.Zero) return false; // Never assume an unreadable process belongs to us.
            try
            {
                var name = new StringBuilder(32768);
                int length = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref length) && SamePath(name.ToString(), target);
            }
            finally { CloseHandle(handle); }
        }

        internal static List<Process> Find(string target)
        {
            var matches = new List<Process>();
            if (string.IsNullOrWhiteSpace(target)) return matches;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                if (Matches(process.Id, target)) matches.Add(process);
                else process.Dispose();
            }
            return matches;
        }
    }
}
