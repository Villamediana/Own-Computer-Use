using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Colo.Core;
using static Colo.Native;

namespace Colo;

/// <summary>Ratón y teclado vía SendInput.</summary>
static class Input
{
    static INPUT Mouse(uint flags, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } },
    };

    static INPUT Key(ushort vk, bool up, bool extended) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    public static void MoveTo(Point p)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(35);
    }

    public static void Click(Point p, string button, bool twice)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        MoveTo(p);
        Send(Mouse(down), Mouse(up));
        if (twice)
        {
            Thread.Sleep(40);
            Send(Mouse(down), Mouse(up));
        }
    }

    public static void Scroll(Point p, string direction, int notches)
    {
        MoveTo(p);
        var horizontal = direction is "left" or "right";
        var sign = direction is "up" or "right" ? 1 : -1;
        for (var i = 0; i < notches; i++)
        {
            Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, sign * 120));
            Thread.Sleep(12);
        }
    }

    public static void Combo(IReadOnlyList<KeyStroke> keys)
    {
        var list = new List<INPUT>();
        foreach (var k in keys) list.Add(Key(k.Vk, false, k.Extended));
        for (var i = keys.Count - 1; i >= 0; i--) list.Add(Key(keys[i].Vk, true, keys[i].Extended));
        Send([.. list]);
    }

    public static void Type(string text)
    {
        var batch = new List<INPUT>();
        void Flush()
        {
            Send([.. batch]);
            batch.Clear();
            Thread.Sleep(6);
        }
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n') { batch.Add(Key(0x0D, false, false)); batch.Add(Key(0x0D, true, false)); }
            else if (c == '\t') { batch.Add(Key(0x09, false, false)); batch.Add(Key(0x09, true, false)); }
            else { batch.Add(Unicode(c, false)); batch.Add(Unicode(c, true)); }
            if (batch.Count >= 60) Flush();
        }
        if (batch.Count > 0) Flush();
    }
}
