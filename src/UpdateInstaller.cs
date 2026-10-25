using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace AutoStart
{
    internal static class UpdateInstaller
    {
        internal static void Launch(string target)
        {
            string helper = Path.Combine(Path.GetDirectoryName(target)!, "AutoStart_update_helper.exe");
            File.Copy(ApplicationPath(), helper, overwrite: true);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--install-update");
            start.ArgumentList.Add(Path.GetFullPath(target));
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var child = Process.Start(start) ?? throw new IOException("Could not start update installer.");
        }

        private static string ApplicationPath() => Environment.ProcessPath ?? throw new IOException("Missing executable path.");

        internal static int Run(string[] args)
        {
            if (args.Length != 3 || !int.TryParse(args[2], out int parentPid) || parentPid <= 0) return 2;
            string target = Path.GetFullPath(args[1]);
            string directory = Path.GetDirectoryName(target)!;
            string ownPath = ApplicationPath();
            if (!TargetProcesses.SamePath(Path.GetDirectoryName(ownPath), directory) ||
                !Path.GetFileName(ownPath).Equals("AutoStart_update_helper.exe", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                TargetProcesses.SamePath(target, ownPath) ||
                Path.GetFileName(target).Equals("AutoStart_new.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(target).Equals("AutoStart_backup.exe", StringComparison.OrdinalIgnoreCase)) return 2;
            void Log(string message)
            {
                try { File.AppendAllText(target + ".update-log.txt", DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
                catch { }
            }
            bool parentExited = false;
            try
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    if (!TargetProcesses.Matches(parentPid, target)) throw new IOException("Installer parent identity mismatch.");
                    if (!parent.WaitForExit(30000)) throw new IOException("Previous AutoStart did not exit; no files replaced.");
                }
                catch (ArgumentException) { /* Parent has already exited. */ }
                parentExited = true;
                return InstallFiles(target, Path.Combine(directory, "AutoStart_new.exe"),
                    Path.Combine(directory, "AutoStart_backup.exe"), Restart, Log) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                if (parentExited)
                    try { Restart(target); } catch (Exception restartError) { Log(restartError.ToString()); }
                return 1;
            }
        }

        private static void Restart(string target)
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target), CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--updated");
            using var process = Process.Start(start) ?? throw new IOException("Could not restart AutoStart.");
        }

        internal static bool InstallFiles(string target, string staged, string backup,
            Action<string> restart, Action<string> log)
        {
            // This method only writes the three explicitly supplied regular files. No recursive cleanup.
            if (TargetProcesses.SamePath(target, staged) || TargetProcesses.SamePath(target, backup) ||
                TargetProcesses.SamePath(staged, backup)) throw new IOException("Update paths must be distinct.");
            byte[] expected = SHA256.HashData(File.ReadAllBytes(staged));
            File.Copy(target, backup, overwrite: true);
            bool replacementAttempted = false;
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { replacementAttempted = true; File.Copy(staged, target, overwrite: true); break; }
                    catch (IOException) when (attempt < 29) { Thread.Sleep(500); }
                }
                if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(File.ReadAllBytes(target))))
                    throw new IOException("Installed file hash mismatch.");
                restart(target);
                log("Installed and restarted verified update; previous executable retained as backup.");
                try { File.Delete(staged); } catch (IOException) { }
                return true;
            }
            catch (Exception ex)
            {
                log("Update failed: " + ex.Message);
                if (replacementAttempted) File.Copy(backup, target, overwrite: true);
                restart(target);
                log("Previous executable restored and restarted.");
                return false;
            }
        }
    }
}
