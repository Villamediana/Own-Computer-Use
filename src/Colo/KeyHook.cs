using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Colo.Core;
using static Colo.Native;

namespace Colo;

/// <summary>
/// Gancho de teclado de bajo nivel (WH_KEYBOARD_LL) para los atajos que se mantienen apretados: RegisterHotKey
/// solo avisa al apretar y no deja usar dos letras. Corre en el hilo de la interfaz (necesita su bucle de
/// mensajes) y tiene que contestar al momento: los avisos se pasan con BeginInvoke.
/// </summary>
sealed class KeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10;
    /// <summary>Tecla sin uso: tapa Win para que al soltar Ctrl+Win no se abra Inicio.</summary>
    const ushort Mask = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc _proc;
    readonly IntPtr _hook;
    public HoldKeys Keys { get; } = new();
    /// <summary>Ajustes está grabando un atajo nuevo: todas las teclas pasan.</summary>
    public bool Paused { get; set; }

    public KeyHook()
    {
        _proc = Proc;
        using var me = Process.GetCurrentProcess();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(me.MainModule?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Write("atajos: no pude instalar el gancho de teclado · " + Marshal.GetLastWin32Error());
        Keys.Replay += Replay;
        Keys.Pressed += id => { if (Keys.HasWin(id)) Tap(Mask); };
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            // Lo que manda Colo (escribir, atajos, teclas reenviadas) no se mira.
            if (!Paused && (k.flags & LLKHF_INJECTED) == 0 && msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
            {
                bool swallow;
                try { swallow = Keys.Key((ushort)k.vkCode, msg is WM_KEYDOWN or WM_SYSKEYDOWN); }
                catch (Exception ex) { Log.Write("atajos: " + ex.Message); swallow = false; }
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static INPUT Key(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = down ? 0 : KEYEVENTF_KEYUP } },
    };

    static void Tap(ushort vk)
    {
        try { Send(Key(vk, true), Key(vk, false)); } catch { }
    }

    static void Replay(IReadOnlyList<(ushort Vk, bool Down)> keys)
    {
        var list = new INPUT[keys.Count];
        for (var i = 0; i < keys.Count; i++) list[i] = Key(keys[i].Vk, keys[i].Down);
        try { Send(list); } catch { }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}
