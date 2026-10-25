using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoStart
{
    // Created/disposed on the message-loop thread, before launching the target.
    // Events catch late popups/restores; polling also catches windows that omit WinEvents.
    internal sealed class TargetWindowMonitor : IDisposable
    {
        private readonly Func<bool> _enabled;
        private readonly Func<int, bool> _matches;
        private readonly Action<string> _log;
        private readonly WinEventProc _callback; // Keep the unmanaged callback alive until unhooked.
        private readonly Timer _timer;
        private readonly IntPtr _showHook;
        private readonly IntPtr _foregroundHook;
        private bool _disposed;
        private bool _handling;
        internal int MinimizedWindows { get; private set; }

        internal TargetWindowMonitor(Func<bool> enabled, Func<int, bool> matches, Action<string> log)
        {
            _enabled = enabled; _matches = matches; _log = log;
            _callback = OnWindowEvent;
            _showHook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _callback, 0, 0, 0);
            _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
            if (_showHook == IntPtr.Zero || _foregroundHook == IntPtr.Zero)
                _log("Window event hook unavailable; background polling remains enabled.");
            _timer = new Timer { Interval = 250 };
            _timer.Tick += (_, _) => Sweep();
            _timer.Start();
        }

        private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr window, int objectId,
            int childId, uint thread, uint time)
        {
            if (objectId == 0 && childId == 0) Minimize(window);
        }

        internal void Sweep()
        {
            if (_disposed || !_enabled()) return;
            EnumWindows((window, _) => { Minimize(window); return true; }, IntPtr.Zero);
        }

        private void Minimize(IntPtr window)
        {
            if (_disposed || _handling || !_enabled() || window == IntPtr.Zero) return;
            try
            {
                _handling = true;
                if (!IsWindowVisible(window) || IsIconic(window) || GetAncestor(window, 2) != window) return;
                GetWindowThreadProcessId(window, out uint pid);
                if (!_matches((int)pid)) return;
                // Do not close a service popup, activate another application, or block on a hung target.
                if (ShowWindowAsync(window, 7))
                {
                    MinimizedWindows++;
                    if (MinimizedWindows <= 3 || MinimizedWindows % 100 == 0)
                        _log("Target window minimized without activation (PID=" + pid + ")");
                }
            }
            catch (Exception ex) { _log("Window monitor: " + ex.Message); }
            finally { _handling = false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop(); _timer.Dispose();
            if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            GC.KeepAlive(_callback);
        }

        private delegate void WinEventProc(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    }
}
