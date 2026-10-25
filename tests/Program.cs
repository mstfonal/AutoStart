using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AutoStart;

static class Tests
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        Interlocked.Increment(ref _checks);
        Console.WriteLine("PASS " + name);
    }

    [STAThread]
    static int Main(string[] args)
    {
        // Fixture-only modes for testing the published installer's actual process/exit/restart path.
        if (args.Length == 1 && args[0] == "--updated")
        { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restarted.txt"), "synthetic restart"); return 0; }
        if (args.Length == 1 && args[0] == "--installer-parent")
        { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "parent-ready.txt"), "synthetic parent"); Thread.Sleep(1800); return 0; }
        try
        {
            Check(TargetProcesses.SamePath(@"C:\Orders\app.exe", @"c:\orders\APP.exe"), "case-insensitive exact executable");
            Check(!TargetProcesses.SamePath(@"C:\Other\app.exe", @"C:\Orders\app.exe"), "same name in another directory rejected");
            Check(!TargetProcesses.SamePath(null, @"C:\Orders\app.exe"), "unreadable identity rejected");
            Check(TargetProcesses.Matches(Environment.ProcessId, Environment.ProcessPath!), "limited-query process identity");
            Check(!TargetProcesses.Matches(int.MaxValue, Environment.ProcessPath!), "nonexistent PID rejected");
            var config = new CyclerConfig { RunMinutes = int.MaxValue, OffMinutes = -2, HeartbeatSeconds = 0, StopWaitSeconds = -1 };
            CyclerContext.NormalizeConfig(config);
            Check(config.RunMinutes == 10080 && config.OffMinutes == 0 && config.HeartbeatSeconds == 1 && config.StopWaitSeconds == 1,
                "invalid configuration cannot overflow or busy-loop");
            var clone = CyclerContext.CloneConfig(config); clone.ExePath = "changed.exe";
            Check(config.ExePath != clone.ExePath, "cycle configuration snapshot is independent");

            byte[] exe = new byte[2048]; exe[0] = (byte)'M'; exe[1] = (byte)'Z';
            string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
            string Release(string tag = "v1.0.20", string name = "AutoStart.exe", string? url = null, string? hash = null, bool prerelease = false)
                => JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease,
                    assets = new[] { new { name, browser_download_url = url ?? "https://github.com/mstfonal/AutoStart/releases/download/" + tag + "/" + name,
                        digest = hash ?? digest } } });
            var info = UpdatePackage.Select(Release(), "1.0.19");
            Check(info != null && info.TagName == "v1.0.20", "new verified release selected");
            Check(UpdatePackage.Select(Release("v1.0.19"), "1.0.19") == null, "same version skipped");
            Check(UpdatePackage.Select(Release("v1.0.18"), "1.0.19") == null, "downgrade skipped");
            Check(UpdatePackage.Select(Release("v1.0.20-beta"), "1.0.19") == null, "invalid version skipped");
            Check(UpdatePackage.Select(Release(prerelease:true), "1.0.19") == null, "prerelease skipped");
            Check(UpdatePackage.Select(Release(name:"Other.exe"), "1.0.19") == null, "unrelated executable not chosen");
            Check(UpdatePackage.Select(Release(url:"https://example.com/AutoStart.exe"), "1.0.19") == null, "foreign download URL rejected");
            Check(UpdatePackage.Select(Release(url:"https://github.com/other/repo/releases/download/v1.0.20/AutoStart.exe"), "1.0.19") == null,
                "other repository rejected");
            Check(UpdatePackage.Select(Release(hash:""), "1.0.19") == null, "missing checksum rejected");
            Check(UpdatePackage.VerifyAndExtract(exe, info!).SequenceEqual(exe), "verified direct EXE retained");
            var damaged = (byte[])exe.Clone(); damaged[100] = 5;
            bool rejected = false;
            try { UpdatePackage.VerifyAndExtract(damaged, info!); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "tampered update rejected before installation");

            byte[] Zip(string name)
            {
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen:true))
                { using var entry = zip.CreateEntry(name).Open(); entry.Write(exe); }
                return output.ToArray();
            }
            var zipBytes = Zip("AutoStart.exe");
            var zipInfo = new UpdateInfo { IsZip = true, Sha256 = Convert.ToHexString(SHA256.HashData(zipBytes)) };
            Check(UpdatePackage.VerifyAndExtract(zipBytes, zipInfo).SequenceEqual(exe), "exact root executable extracted without filesystem writes");
            var badZip = Zip("../AutoStart.exe"); zipInfo.Sha256 = Convert.ToHexString(SHA256.HashData(badZip));
            rejected = false;
            try { UpdatePackage.VerifyAndExtract(badZip, zipInfo); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "path-traversal ZIP rejected");

            // All installer operations stay in a new OS-temp directory. No installed application is run.
            string temp = Path.Combine(Path.GetTempPath(), "AutoStart-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(temp);
            string target = Path.Combine(temp, "Önal & test's app.exe"), staged = Path.Combine(temp,"new.exe"), backup = Path.Combine(temp,"old.exe");
            byte[] old = { 1, 2, 3 };
            File.WriteAllBytes(target, old); File.WriteAllBytes(staged, exe);
            int starts = 0;
            Check(UpdateInstaller.InstallFiles(target, staged, backup, _ => starts++, _ => { }), "installer success");
            Check(File.ReadAllBytes(target).SequenceEqual(exe) && File.ReadAllBytes(backup).SequenceEqual(old) && starts == 1,
                "new bytes installed, previous bytes backed up, exactly one restart");
            Check(!File.Exists(staged), "staging file removed only after successful restart");
            File.WriteAllBytes(target, old); File.WriteAllBytes(staged, exe); starts = 0;
            Check(!UpdateInstaller.InstallFiles(target, staged, backup, _ => { if (++starts == 1) throw new IOException("synthetic launch failure"); }, _ => { }),
                "restart failure triggers rollback");
            Check(File.ReadAllBytes(target).SequenceEqual(old) && starts == 2 && File.Exists(staged), "rollback restores exact old bytes and retains staged recovery file");
            rejected = false;
            try { UpdateInstaller.InstallFiles(target, target, backup, _ => {}, _ => {}); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(target).SequenceEqual(old), "aliased updater paths rejected without mutation");
            Check(UpdateInstaller.Run(new[] { "--install-update", target, "invalid" }) == 2, "malformed installer invocation rejected");

            NativeWindows();
            Console.WriteLine("ALL " + _checks + " CHECKS PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void NativeWindows()
    {
        // A non-input desktop keeps synthetic forms off the user's screen and taskbar.
        IntPtr desktop = CreateDesktop("AutoStartTests-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Exception("Could not create isolated test desktop: " + Marshal.GetLastWin32Error());
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Exception("Could not select isolated test desktop.");
                using var context = new WindowsFormsSynchronizationContext();
                bool uiPosted = false;
                var background = new Thread(() => context.Post(_ => uiPosted = true, null));
                background.Start(); background.Join();
                using var main = new TestForm { Text = "Synthetic order main" };
                using var popup = new TestForm { Text = "LP Online Order Services" };
                bool enabled = true, ownsProcess = true;
                using var monitor = new TargetWindowMonitor(() => enabled, pid => ownsProcess && pid == Environment.ProcessId, _ => {});
                main.Show();
                int step = 0;
                using var timer = new System.Windows.Forms.Timer { Interval = 400 };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        switch (step++)
                        {
                            case 0:
                                Check(uiPosted, "UI context created before message loop delivers worker callbacks");
                                Check(TargetWindowMonitor.IsIconic(main.Handle), "native initial window minimized"); main.WindowState = FormWindowState.Normal; break;
                            case 1: Check(TargetWindowMonitor.IsIconic(main.Handle), "native restored window minimized again"); popup.Show(); break;
                            case 2: Check(TargetWindowMonitor.IsIconic(popup.Handle), "native late LP popup minimized without closing"); enabled = false; main.WindowState = FormWindowState.Normal; break;
                            case 3: Check(!TargetWindowMonitor.IsIconic(main.Handle), "disabled background option respected"); enabled = true; ownsProcess = false; break;
                            case 4: Check(!TargetWindowMonitor.IsIconic(main.Handle), "non-target PID left untouched"); ownsProcess = true; monitor.Sweep(); break;
                            case 5: Check(TargetWindowMonitor.IsIconic(main.Handle), "fallback enumeration enforces background"); monitor.Dispose(); main.WindowState = FormWindowState.Normal; break;
                            case 6: Check(!TargetWindowMonitor.IsIconic(main.Handle), "disposed hooks and timer stop controlling windows"); timer.Stop(); Application.ExitThread(); break;
                        }
                    }
                    catch (Exception ex) { failure = ex; timer.Stop(); Application.ExitThread(); }
                };
                timer.Start(); Application.Run();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(15000)) throw new Exception("Native test timed out.");
        CloseDesktop(desktop);
        if (failure != null) throw failure;
    }
    sealed class TestForm : Form
    {
        public TestForm() { ShowInTaskbar = false; Width = 200; Height = 100; }
        protected override bool ShowWithoutActivation => true;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
}
