using System.Text;
using System.Text.Json;

namespace Colo.Core;

/// <summary>
/// Una entrada del cofre. Secret va cifrado (en Windows con DPAPI: solo este usuario en este PC lo abre).
/// Name, Kind, User, Host y Where sí los ve el modelo, para saber qué hay y dónde va; el secreto nunca.
/// </summary>
public sealed record VaultEntry(string Name, string Kind, string User, string Host, string Where, string Secret, DateTime Added);

/// <summary>
/// Cofre de contraseñas, claves de ssh, tokens… Colo los guarda (lo decide el modelo o lo pide el usuario, o a
/// mano en Ajustes) y los escribe donde hace falta sin que el secreto pase por el modelo ni quede en el registro.
/// </summary>
public sealed class Vault
{
    public const int MaxEntries = 200;
    public static readonly string[] Kinds = ["password", "ssh", "login", "token", "pin", "note"];

    readonly string _path;
    readonly Func<byte[], byte[]> _protect, _unprotect;
    readonly object _lock = new();
    List<VaultEntry> _entries = [];

    /// <summary>Se dispara con cada secreto nuevo, para borrarlo de donde haya quedado escrito (el registro).</summary>
    public event Action<string>? Saved;

    public Vault(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        _path = path;
        _protect = protect;
        _unprotect = unprotect;
        try { if (File.Exists(path)) _entries = JsonSerializer.Deserialize<List<VaultEntry>>(File.ReadAllText(path)) ?? []; }
        catch { _entries = []; }
    }

    public IReadOnlyList<VaultEntry> Entries { get { lock (_lock) return [.. _entries]; } }

    /// <summary>Lo que el modelo puede ver de una entrada.</summary>
    public static string Describe(VaultEntry e)
    {
        var sb = new StringBuilder($"«{e.Name}» {e.Kind}");
        if (e.User.Length > 0 || e.Host.Length > 0)
            sb.Append(' ').Append(e.User).Append(e.User.Length > 0 && e.Host.Length > 0 ? "@" : "").Append(e.Host);
        if (e.Where.Length > 0) sb.Append($" (en {e.Where})");
        return sb.ToString();
    }

    /// <summary>Las líneas del turno: nombres y para qué sirven, sin secretos.</summary>
    public IReadOnlyList<string> Lines() { lock (_lock) return _entries.Select(Describe).ToList(); }

    /// <summary>replace = false: si ya hay uno con ese nombre no se toca (el modelo pisó así una contraseña ssh con un e-mail).</summary>
    public string Save(string name, string kind, string user, string host, string where, string secret, bool replace = true)
    {
        name = name.Trim();
        if (name.Length == 0) return "error: falta name";
        if (secret.Length == 0) return "error: falta el secreto";
        if (secret.Length > 10_000) return "error: secreto demasiado largo";
        kind = Kinds.Contains(kind) ? kind : "password";
        var cipher = Convert.ToBase64String(_protect(Encoding.UTF8.GetBytes(secret)));
        bool replaced;
        lock (_lock)
        {
            var i = _entries.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            replaced = i >= 0;
            if (replaced && !replace)
                return $"error: ya existe {Describe(_entries[i])}. Pregúntale si quiere reemplazarlo; solo con su sí, save con replace=true (o usa otro name)";
            if (!replaced && _entries.Count >= MaxEntries) return "error: el cofre está lleno";
            var entry = new VaultEntry(name, kind, user.Trim(), host.Trim(), where.Trim(), cipher, DateTime.Now);
            if (replaced) _entries[i] = entry; else _entries.Add(entry);
            Persist();
        }
        Saved?.Invoke(secret);
        return replaced ? $"ok: actualicé «{name}» en el cofre (el secreto no se muestra)" : $"ok: «{name}» guardado en el cofre (el secreto no se muestra)";
    }

    /// <summary>El secreto en claro, solo para escribirlo o copiarlo. null si no existe o no se puede abrir.</summary>
    public string? Reveal(string name, out VaultEntry? entry)
    {
        entry = Find(name);
        if (entry is null) return null;
        try { return Encoding.UTF8.GetString(_unprotect(Convert.FromBase64String(entry.Secret))); }
        catch { return null; }
    }

    /// <summary>Por nombre exacto o, si no, el único que lo contiene (sin acentos).</summary>
    public VaultEntry? Find(string name)
    {
        var want = Extract.Fold(name.Trim());
        if (want.Length == 0) return null;
        lock (_lock)
        {
            var exact = _entries.FirstOrDefault(e => Extract.Fold(e.Name) == want);
            if (exact is not null) return exact;
            var partial = _entries.Where(e => Extract.Fold(e.Name).Contains(want) || (e.Host.Length > 0 && Extract.Fold(e.Host) == want)).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }
    }

    public string Forget(string name)
    {
        lock (_lock)
        {
            var e = Find(name);
            if (e is null) return $"error: no hay «{name}» en el cofre";
            _entries.Remove(e);
            Persist();
            return $"ok: «{e.Name}» borrado del cofre";
        }
    }

    void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
        File.Move(tmp, _path, overwrite: true);
    }
}
