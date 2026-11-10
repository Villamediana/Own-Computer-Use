using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Colo;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40,
        MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size,
        IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    static volatile bool _noVsync;

    /// <summary>
    /// Espera al próximo refresco de la pantalla (el del compositor): las animaciones van al ritmo del monitor,
    /// 60, 120 o 144 Hz, sin el salto de 15,6 ms del reloj de Windows. Sin compositor (Wine), ~7 ms.
    /// </summary>
    public static void WaitFrame()
    {
        if (!_noVsync)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                var ok = DwmFlush() == 0;
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > 60) _noVsync = true;           // pantalla apagada o sesión rara: no fiarse
                if (ok && ms > 1) return;
            }
            catch { _noVsync = true; }
        }
        Thread.Sleep(7);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    public const int GWL_EXSTYLE = -20;

    public static void SetClickThrough(IntPtr h, bool on)
    {
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint size);

    /// <summary>El nombre corto (8.3) de una ruta que existe; la misma si el disco no los guarda.</summary>
    public static string ShortPath(string path)
    {
        var sb = new System.Text.StringBuilder(1024);
        var n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x10, GW_OWNER = 4;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr h);

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ProcessName(IntPtr h)
    {
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    /// <summary>Ventanas normales visibles (con título, sin dueño, que no son de herramientas), de la de arriba hacia abajo.</summary>
    public static System.Collections.Generic.List<IntPtr> AppWindows()
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0 || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 || IsOwnWindow(h)) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Lo que el programa de la ventana activa abrió encima de ella como ventana aparte: menús contextuales,
    /// sugerencias de autocompletar (el «Para» del Outlook clásico, la barra del navegador), listas desplegables.
    /// UI Automation no los ve dentro de la ventana activa. Los más altos primero.
    /// </summary>
    public static System.Collections.Generic.List<IntPtr> PopupsAbove(IntPtr front, int max = 4)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        if (front == IntPtr.Zero) return list;
        GetWindowThreadProcessId(front, out var pid);
        EnumWindows((h, _) =>
        {
            // Orden de pantalla: al llegar a la ventana activa, lo que sigue está debajo.
            if (h == front) return false;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || p == Environment.ProcessId) return true;
            // La barra de tareas es del mismo proceso que el Explorador y siempre está encima: ya va aparte.
            if (ClassName(h).StartsWith("Shell_", StringComparison.Ordinal)) return true;
            if (!GetWindowRect(h, out var r) || r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
            list.Add(h);
            return list.Count < max;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Trae una ventana al frente aunque Windows limite el cambio de foco entre programas.</summary>
    public static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return true;
        // Windows solo deja cambiar el foco a quien recibió la última entrada: un ALT sintético lo habilita.
        var alt = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12 } } };
        var altUp = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x12, dwFlags = KEYEVENTF_KEYUP } } };
        try { Send(alt, altUp); } catch { }
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return true;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { AttachThreadInput(me, fgThread, false); }
        return GetForegroundWindow() == h;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
            throw new InvalidOperationException("Windows bloqueó la entrada (¿ventana de administrador o pantalla segura?).");
    }

    public static string WindowLabel(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(escritorio)";
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        var title = sb.ToString();
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return string.IsNullOrEmpty(title) ? p.ProcessName : $"{title}  [{p.ProcessName}]";
        }
        catch { return title; }
    }

    public static bool IsOwnWindow(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        return pid == Environment.ProcessId;
    }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

    /// <summary>La ventana de primer nivel que está bajo ese punto (la de la app, no su control hijo).</summary>
    public static IntPtr RootAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { x = x, y = y });
        return h == IntPtr.Zero ? h : GetAncestor(h, 2); // GA_ROOT
    }

    public static bool SameProcess(IntPtr a, IntPtr b)
    {
        GetWindowThreadProcessId(a, out var pa);
        GetWindowThreadProcessId(b, out var pb);
        return pa != 0 && pa == pb;
    }

    /// <summary>Esquinas redondeadas de Windows 11; en Windows 10 no hace nada.</summary>
    public static void RoundCorners(IntPtr h)
    {
        var pref = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(h, 33, ref pref, sizeof(int)); } catch { }
    }
}
