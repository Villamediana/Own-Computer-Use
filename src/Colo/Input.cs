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

    /// <summary>Lleva el ratón hasta p deslizándolo (ver <see cref="Glide"/>) y deja que la app note el hover.</summary>
    public static void MoveTo(Point p)
    {
        GetCursorPos(out var cur);
        Glide(new Point(cur.x, cur.y), p);
        Thread.Sleep(25);
    }

    /// <summary>
    /// Mueve el ratón de verdad de from a to, un paso por refresco de pantalla, con la curva de <see cref="Motion"/>.
    /// <paramref name="frame"/> recibe cada punto (el cursor azul lo sigue). Bloquea lo que dure el gesto.
    /// </summary>
    public static void Glide(Point from, Point to, Action<Point>? frame = null, bool curved = true, double slower = 1)
    {
        var ms = Motion.Duration(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2))) * slower;
        if (ms > 0)
        {
            timeBeginPeriod(1);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Point last = from;
                for (var t = 0.0; t < 1; t = sw.Elapsed.TotalMilliseconds / ms)
                {
                    var (x, y) = Motion.At(from.X, from.Y, to.X, to.Y, t, curved);
                    var q = new Point((int)Math.Round(x), (int)Math.Round(y));
                    if (q != last || t == 0)
                    {
                        SetCursorPos(q.X, q.Y);
                        frame?.Invoke(q);
                        last = q;
                    }
                    WaitFrame();
                }
            }
            finally { timeEndPeriod(1); }
        }
        SetCursorPos(to.X, to.Y);
        frame?.Invoke(to);
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

    /// <summary>Clic seco, sin deslizar (para el dictado: tiene que ser instantáneo); el ratón vuelve a donde estaba.</summary>
    public static void ClickNow(Point p)
    {
        GetCursorPos(out var cur);
        SetCursorPos(p.X, p.Y);
        Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));
        Thread.Sleep(30);
        SetCursorPos(cur.x, cur.y);
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

    public static void Drag(Point from, Point to, Action<Point>? follow)
    {
        MoveTo(from);
        Send(Mouse(MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(60);
        // En recta y algo más lento que un clic: muchas apps miran el camino, no solo el final.
        Glide(from, to, follow, curved: false, slower: 1.35);
        Thread.Sleep(50);
        Send(Mouse(MOUSEEVENTF_LEFTUP));
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
