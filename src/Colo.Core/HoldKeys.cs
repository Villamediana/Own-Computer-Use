namespace Colo.Core;

/// <summary>
/// Atajos que se mantienen apretados (hablar, dictar). Valen dos teclas cualesquiera, también dos letras: se
/// aprieta una y luego la otra y queda activo mientras estén abajo; al levantar una, se suelta.
/// Una letra del atajo se retiene al bajar; si no llega la otra (se suelta sola o viene otra tecla), se
/// reenvía tal cual y escribir sigue igual. Si con el atajo activo se aprieta otra tecla (Ctrl+Win+D…),
/// era otro atajo: se cancela sin transcribir.
/// Recibe las teclas del gancho de teclado de Windows y dice si cada una se traga.
/// </summary>
public sealed class HoldKeys
{
    readonly Dictionary<string, HashSet<ushort>> _combos = [];
    readonly HashSet<ushort> _down = [];
    // Teclas del atajo retenidas (vk real, en orden) hasta saber si viene la otra.
    readonly List<ushort> _pending = [];
    // Teclas del atajo que siguen abajo después de soltarlo: su «up» tampoco pasa.
    readonly HashSet<ushort> _swallowUp = [];
    string? _active;

    public event Action<string>? Pressed;
    public event Action<string>? Released;
    public event Action<string>? Cancelled;
    /// <summary>Teclas que hay que reenviar en este orden (true = bajar).</summary>
    public event Action<IReadOnlyList<(ushort Vk, bool Down)>>? Replay;

    public string? Active => _active;

    /// <summary>Pone (o quita, con combo vacío) un atajo. false si no es válido.</summary>
    public bool Set(string id, string? combo)
    {
        Reset();
        if (string.IsNullOrWhiteSpace(combo)) { _combos.Remove(id); return true; }
        if (!Keys.Hold(combo, out var vks)) return false;
        _combos[id] = vks;
        return true;
    }

    public bool HasWin(string id) => _combos.TryGetValue(id, out var c) && c.Contains(0x5B);

    /// <summary>Una tecla del gancho (no inyectada). true = no pasa a la ventana.</summary>
    public bool Key(ushort rawVk, bool down)
    {
        var vk = Keys.Generic(rawVk);
        var modifier = Keys.IsModifier(vk);
        return down ? Down(rawVk, vk, modifier) : Up(rawVk, vk, modifier);
    }

    bool Down(ushort rawVk, ushort vk, bool modifier)
    {
        // La repetición automática de una tecla mantenida llega como otro «down».
        var repeat = !_down.Add(vk);
        if (_active is { } id)
        {
            if (_combos[id].Contains(vk)) return !modifier;
            _active = null;
            foreach (var k in _combos[id]) if (_down.Contains(k) && !Keys.IsModifier(k)) _swallowUp.Add(k);
            Cancelled?.Invoke(id);
            return false;
        }
        if (repeat) return _pending.Contains(rawVk) || _swallowUp.Contains(vk);
        foreach (var (cid, keys) in _combos)
            if (_down.SetEquals(keys))
            {
                _active = cid;
                _pending.Clear();
                Pressed?.Invoke(cid);
                return !modifier;
            }
        if (!modifier && _combos.Values.Any(c => c.Contains(vk) && _down.IsSubsetOf(c)))
        {
            _pending.Add(rawVk);
            return true;
        }
        if (_pending.Count == 0) return false;
        // Venía otra tecla: lo retenido sale primero y esta detrás, en su orden.
        var replay = _pending.Select(k => (k, true)).Append((rawVk, true)).ToList();
        _pending.Clear();
        Replay?.Invoke(replay);
        return true;
    }

    bool Up(ushort rawVk, ushort vk, bool modifier)
    {
        _down.Remove(vk);
        if (_active is { } id && _combos[id].Contains(vk))
        {
            _active = null;
            foreach (var k in _combos[id]) if (_down.Contains(k) && !Keys.IsModifier(k)) _swallowUp.Add(k);
            Released?.Invoke(id);
            return !modifier;
        }
        if (_swallowUp.Remove(vk)) return true;
        if (!_pending.Contains(rawVk)) return false;
        // Se soltó sin que llegara la otra: era una letra normal.
        var replay = _pending.Select(k => (k, true)).Append((rawVk, false)).ToList();
        _pending.Clear();
        Replay?.Invoke(replay);
        return true;
    }

    /// <summary>Un «up» perdido (la sesión se bloqueó con las teclas abajo…) deja el estado limpio.</summary>
    public void Reset()
    {
        _down.Clear();
        _pending.Clear();
        _swallowUp.Clear();
        if (_active is { } id)
        {
            _active = null;
            Cancelled?.Invoke(id);
        }
    }
}
