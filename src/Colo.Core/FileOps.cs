using System.IO.Compression;
using System.Text;

namespace Colo.Core;

/// <summary>Papelera del sistema: borrar sin perder y poder recuperar.</summary>
public interface IRecycleBin
{
    void Recycle(string path);
    /// <summary>Devuelve a su sitio lo último borrado desde esa ruta. false = no está en la papelera.</summary>
    bool Restore(string originalPath);
}

/// <summary>
/// Operaciones con archivos que pide el usuario (renombrar, mover, copiar, carpetas, papelera, zip) con registro
/// para deshacer la última. Nada se sobrescribe: si el destino existe, ese archivo se salta y se dice.
/// Rutas relativas = la carpeta abierta en el Explorador (BaseDir).
/// </summary>
public sealed class FileOps(IRecycleBin bin)
{
    // Created: algo nuevo que hicimos (copia, zip); Folder: carpeta creada para meter cosas, se quita solo si sigue vacía.
    enum Kind { Moved, Created, Folder, Recycled }
    sealed record Step(Kind Kind, string Path, string? From = null);

    const int MaxUndo = 20;
    readonly List<List<Step>> _undo = [];
    readonly object _lock = new();

    public string Run(string action, string? baseDir, IReadOnlyList<string> paths, string to, IReadOnlyList<string> names, string pattern)
    {
        try
        {
            return action switch
            {
                "list" => List(Resolve(paths.FirstOrDefault() ?? "", baseDir, folderDefault: true), pattern),
                "rename" => Rename(Expand(paths, baseDir), names),
                "move" => MoveOrCopy(Expand(paths, baseDir), Resolve(to, baseDir), copy: false),
                "copy" => MoveOrCopy(Expand(paths, baseDir), Resolve(to, baseDir), copy: true),
                "new_folder" => NewFolder(Resolve(paths.FirstOrDefault() ?? to, baseDir)),
                "delete" => Delete(Expand(paths, baseDir)),
                "zip" => Zip(Expand(paths, baseDir), to, baseDir),
                "unzip" => Unzip(Resolve(paths.FirstOrDefault() ?? "", baseDir), to, baseDir),
                "undo" => Undo(),
                _ => $"error: acción desconocida «{action}»",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>Ruta absoluta: «~» es la carpeta del usuario; relativa, la carpeta abierta.</summary>
    public static string Resolve(string path, string? baseDir, bool folderDefault = false)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
        {
            if (folderDefault && baseDir is { Length: > 0 }) return baseDir;
            throw new ArgumentException("falta la ruta");
        }
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            var rest = path.Length > 2 ? path[2..].Replace('\\', '/') : "";
            var cut = rest.IndexOf('/');
            var first = cut < 0 ? rest : rest[..cut];
            // «~/Documentos», «~/Escritorio»…: la carpeta de verdad, aunque esté en OneDrive o tenga otro nombre.
            path = Known(first) is { } real
                ? Path.Combine(real, cut < 0 ? "" : rest[(cut + 1)..])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest);
        }
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        if (baseDir is not { Length: > 0 })
            throw new ArgumentException($"«{path}» no es una ruta completa y no hay carpeta abierta en el Explorador");
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    static string? Known(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? real = Extract.Fold(name) switch
        {
            "desktop" or "escritorio" or "area de trabalho" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documents" or "documentos" or "mis documentos" or "meus documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" or "descargas" => Path.Combine(home, "Downloads"),
            "pictures" or "imagenes" or "imagens" or "fotos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" or "musica" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null,
        };
        return string.IsNullOrEmpty(real) ? null : real;
    }

    /// <summary>Las rutas pedidas, con comodines (*.pdf) en el nombre ya resueltos.</summary>
    public static List<string> Expand(IReadOnlyList<string> paths, string? baseDir)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            var full = Resolve(p, baseDir);
            var name = Path.GetFileName(full);
            if (name.Contains('*') || name.Contains('?'))
            {
                var dir = Path.GetDirectoryName(full)!;
                if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"no existe la carpeta {dir}");
                result.AddRange(Directory.EnumerateFileSystemEntries(dir, name).Order(StringComparer.OrdinalIgnoreCase));
            }
            else result.Add(full);
        }
        if (result.Count == 0) throw new ArgumentException(paths.Count == 0 ? "faltan paths" : "ningún archivo coincide");
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    static string List(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return $"error: no existe la carpeta {dir}";
        var info = new DirectoryInfo(dir);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var all = info.EnumerateFileSystemInfos(pattern.Length > 0 ? pattern : "*", opts)
            .OrderBy(e => e is FileInfo).ThenByDescending(e => e.LastWriteTime).ToList();
        if (all.Count == 0) return $"ok: {dir} — vacía" + (pattern.Length > 0 ? $" (con {pattern})" : "");
        var sb = new StringBuilder($"ok: {dir} — {all.Count} elementos\n");
        foreach (var e in all.Take(80))
            sb.AppendLine(e is FileInfo f
                ? $"{f.Name}  ({f.LastWriteTime:dd/MM/yyyy HH:mm}, {Size(f.Length)})"
                : $"{e.Name}\\  ({e.LastWriteTime:dd/MM/yyyy})");
        if (all.Count > 80) sb.AppendLine($"…y {all.Count - 80} más (usa pattern)");
        return sb.ToString().TrimEnd();
    }

    static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0} KB" : b < 1 << 30 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    string Rename(List<string> paths, IReadOnlyList<string> names)
    {
        if (names.Count != paths.Count) return $"error: hacen falta tantos names ({names.Count}) como archivos ({paths.Count})";
        var done = new List<Step>();
        var report = new List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            var from = paths[i];
            var name = names[i].Trim();
            if (!Exists(from)) { report.Add($"no existe {from}"); continue; }
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToArray()) >= 0)
            { report.Add($"nombre no válido «{name}»"); continue; }
            // Sin extensión en el nombre nuevo: se queda la que tenía (renombrar no cambia el tipo).
            if (File.Exists(from) && Path.GetExtension(name).Length == 0) name += Path.GetExtension(from);
            var to = Path.Combine(Path.GetDirectoryName(from)!, name);
            if (string.Equals(from, to, StringComparison.Ordinal)) continue;
            var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(to)) { report.Add($"ya existe {name}"); continue; }
            if (caseOnly)
            {
                var tmp = from + ".colo-tmp";
                MoveEntry(from, tmp);
                MoveEntry(tmp, to);
            }
            else MoveEntry(from, to);
            done.Add(new Step(Kind.Moved, to, from));
            report.Add($"{Path.GetFileName(from)} → {name}");
        }
        return Finish(done, report, "renombrado");
    }

    string MoveOrCopy(List<string> paths, string to, bool copy)
    {
        var done = new List<Step>();
        var report = new List<string>();
        if (File.Exists(to)) return $"error: el destino {to} es un archivo, no una carpeta";
        var inside = paths.Where(from => Directory.Exists(from)
            && (to + Path.DirectorySeparatorChar).StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var from in inside) report.Add($"no puedo meter {from} dentro de sí misma");
        paths = paths.Except(inside).ToList();
        if (paths.Count > 0 && !Directory.Exists(to))
        {
            Directory.CreateDirectory(to);
            done.Add(new Step(Kind.Folder, to));
            report.Add($"creé la carpeta {to}");
        }
        foreach (var from in paths)
        {
            if (!Exists(from)) { report.Add($"no existe {from}"); continue; }
            var dest = Path.Combine(to, Path.GetFileName(from.TrimEnd('\\', '/')));
            if (Exists(dest)) { report.Add($"ya existe {dest}, no lo toqué"); continue; }
            if (copy)
            {
                if (Directory.Exists(from)) CopyDir(from, dest);
                else File.Copy(from, dest);
                done.Add(new Step(Kind.Created, dest));
            }
            else
            {
                // Entre discos Directory.Move no sirve: se copia y se borra el original.
                if (Directory.Exists(from) && !SameRoot(from, dest)) { CopyDir(from, dest); Directory.Delete(from, true); }
                else MoveEntry(from, dest);
                done.Add(new Step(Kind.Moved, dest, from));
            }
            report.Add($"{Path.GetFileName(from)} → {to}");
        }
        return Finish(done, report, copy ? "copiado" : "movido");
    }

    static bool SameRoot(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    string NewFolder(string path)
    {
        if (Exists(path)) return $"ok: ya existía {path}";
        Directory.CreateDirectory(path);
        return Finish([new Step(Kind.Folder, path)], [$"creada {path}"], "creada");
    }

    string Delete(List<string> paths)
    {
        var done = new List<Step>();
        var report = new List<string>();
        foreach (var p in paths)
        {
            if (!Exists(p)) { report.Add($"no existe {p}"); continue; }
            bin.Recycle(p);
            done.Add(new Step(Kind.Recycled, p));
            report.Add($"{Path.GetFileName(p)} → papelera");
        }
        return Finish(done, report, "a la papelera");
    }

    string Zip(List<string> paths, string to, string? baseDir)
    {
        var first = paths[0];
        var zip = to.Trim().Length > 0 ? Resolve(to, baseDir)
            : Path.Combine(Path.GetDirectoryName(first)!, (paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(Path.GetDirectoryName(first)!)) + ".zip");
        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip += ".zip";
        if (Exists(zip)) return $"error: ya existe {zip}";
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var p in paths)
            {
                if (File.Exists(p)) archive.CreateEntryFromFile(p, Path.GetFileName(p));
                else if (Directory.Exists(p))
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        archive.CreateEntryFromFile(f, Path.Combine(Path.GetFileName(p), Path.GetRelativePath(p, f)).Replace('\\', '/'));
            }
        return Finish([new Step(Kind.Created, zip)], [$"{paths.Count} → {zip}"], "comprimido");
    }

    string Unzip(string zip, string to, string? baseDir)
    {
        if (!File.Exists(zip)) return $"error: no existe {zip}";
        var dest = to.Trim().Length > 0 ? Resolve(to, baseDir) : Path.Combine(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip));
        if (Exists(dest)) return $"error: ya existe {dest} (elige otro to)";
        ZipFile.ExtractToDirectory(zip, dest);
        return Finish([new Step(Kind.Created, dest)], [$"{Path.GetFileName(zip)} → {dest}"], "descomprimido");
    }

    string Finish(List<Step> done, List<string> report, string verb)
    {
        if (done.Count > 0)
            lock (_lock)
            {
                _undo.Add(done);
                if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            }
        var head = done.Count == 0 ? "error: no hice nada" : $"ok: {verb} ({done.Count}). Se puede deshacer con undo";
        return head + "\n" + string.Join("\n", report.Take(40)) + (report.Count > 40 ? $"\n…y {report.Count - 40} más" : "");
    }

    /// <summary>Deshace la última operación entera (todo lo que se hizo en esa llamada), en orden inverso.</summary>
    public string Undo()
    {
        List<Step> last;
        lock (_lock)
        {
            if (_undo.Count == 0) return "error: no hay nada que deshacer";
            last = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
        }
        var report = new List<string>();
        foreach (var s in Enumerable.Reverse(last))
        {
            try
            {
                switch (s.Kind)
                {
                    case Kind.Moved when Exists(s.Path) && !Exists(s.From!):
                        if (Directory.Exists(s.Path) && !SameRoot(s.Path, s.From!)) { CopyDir(s.Path, s.From!); Directory.Delete(s.Path, true); }
                        else MoveEntry(s.Path, s.From!);
                        report.Add($"{Path.GetFileName(s.Path)} volvió a {s.From}");
                        break;
                    case Kind.Folder when Directory.Exists(s.Path):
                        // Lo de dentro ya volvió a su sitio; si quedó algo que no es nuestro, la carpeta se deja.
                        if (Directory.EnumerateFileSystemEntries(s.Path).Any()) { report.Add($"dejé {s.Path} (no está vacía)"); break; }
                        Directory.Delete(s.Path);
                        report.Add($"quité la carpeta {Path.GetFileName(s.Path)}");
                        break;
                    case Kind.Created when Exists(s.Path):
                        bin.Recycle(s.Path);
                        report.Add($"{Path.GetFileName(s.Path)} → papelera");
                        break;
                    case Kind.Recycled:
                        report.Add(bin.Restore(s.Path) ? $"recuperé {s.Path}" : $"no encontré {Path.GetFileName(s.Path)} en la papelera");
                        break;
                    default:
                        report.Add($"no pude deshacer {Path.GetFileName(s.Path)} (cambió desde entonces)");
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add($"falló {Path.GetFileName(s.Path)}: {ex.Message}");
            }
        }
        return "ok: deshecho\n" + string.Join("\n", report);
    }
}
