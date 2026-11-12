using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusApp;

/// <summary>
/// Brings a pop-up (a lesson, practice round or quiz) to the front even when you're working in another program. Windows
/// normally blocks this and only flashes the taskbar button; joining the foreground program's input for a moment lets the
/// window come up in front, the way it should for a practice round.
/// </summary>
public static class WindowFront
{
    public static void Bring(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) { w.Activate(); return; }
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            var fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool joined = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                ShowWindow(hwnd, 5); // SW_SHOW
                SetForegroundWindow(hwnd);
            }
            finally { if (joined) AttachThreadInput(me, fgThread, false); }
            // A moment on top of everything, so it isn't left behind a topmost window (like the docked task window).
            bool wasTop = w.Topmost;
            w.Topmost = true;
            w.Activate();
            if (!wasTop) w.Dispatcher.BeginInvoke(new Action(() => w.Topmost = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex) { Log.Error("WindowFront", ex); w.Activate(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
