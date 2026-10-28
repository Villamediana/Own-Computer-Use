namespace Colo.Core;

public sealed record KeyStroke(ushort Vk, bool Extended);

/// <summary>Convierte "ctrl+shift+t" en códigos de tecla virtual de Windows.</summary>
public static class Keys
{
    static readonly Dictionary<string, KeyStroke> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new(0x11, false), ["control"] = new(0x11, false),
        ["shift"] = new(0x10, false),
        ["alt"] = new(0x12, false),
        ["win"] = new(0x5B, true), ["windows"] = new(0x5B, true), ["super"] = new(0x5B, true), ["meta"] = new(0x5B, true), ["cmd"] = new(0x5B, true),
        ["enter"] = new(0x0D, false), ["return"] = new(0x0D, false),
        ["tab"] = new(0x09, false),
        ["esc"] = new(0x1B, false), ["escape"] = new(0x1B, false),
        ["space"] = new(0x20, false), ["espacio"] = new(0x20, false),
        ["backspace"] = new(0x08, false),
        ["delete"] = new(0x2E, true), ["del"] = new(0x2E, true), ["supr"] = new(0x2E, true),
        ["insert"] = new(0x2D, true),
        ["home"] = new(0x24, true), ["end"] = new(0x23, true),
        ["pageup"] = new(0x21, true), ["pgup"] = new(0x21, true),
        ["pagedown"] = new(0x22, true), ["pgdn"] = new(0x22, true),
        ["up"] = new(0x26, true), ["down"] = new(0x28, true), ["left"] = new(0x25, true), ["right"] = new(0x27, true),
        ["printscreen"] = new(0x2C, true),
        ["capslock"] = new(0x14, false),
        ["volumeup"] = new(0xAF, true), ["volumedown"] = new(0xAE, true), ["volumemute"] = new(0xAD, true),
        ["playpause"] = new(0xB3, true), ["nexttrack"] = new(0xB0, true), ["prevtrack"] = new(0xB1, true),
        ["plus"] = new(0xBB, false), ["minus"] = new(0xBD, false),
        [","] = new(0xBC, false), ["."] = new(0xBE, false), ["-"] = new(0xBD, false), ["="] = new(0xBB, false),
        ["/"] = new(0xBF, false), [";"] = new(0xBA, false), ["["] = new(0xDB, false), ["]"] = new(0xDD, false),
        ["\\"] = new(0xDC, false), ["'"] = new(0xDE, false), ["`"] = new(0xC0, false),
    };

    public static List<KeyStroke> Parse(string combo)
    {
        var parts = combo.Trim().Split('+', StringSplitOptions.TrimEntries);
        // "ctrl++" → la última parte vacía significa la tecla '+'
        var result = new List<KeyStroke>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0)
            {
                if (i == parts.Length - 1 && i > 0) { result.Add(Named["plus"]); break; }
                continue;
            }
            result.Add(One(p));
        }
        if (result.Count == 0) throw new ArgumentException($"tecla vacía: «{combo}»");
        return result;
    }

    static KeyStroke One(string p)
    {
        if (Named.TryGetValue(p, out var k)) return k;
        if (p.Length == 1)
        {
            var c = char.ToUpperInvariant(p[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return new(c, false);
        }
        if ((p[0] is 'f' or 'F') && int.TryParse(p[1..], out var n) && n is >= 1 and <= 24)
            return new((ushort)(0x70 + n - 1), false);
        throw new ArgumentException($"tecla desconocida: «{p}»");
    }
}
