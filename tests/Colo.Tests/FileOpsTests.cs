using Colo.Core;
using Xunit;

namespace Colo.Tests;

public sealed class FileOpsTests : IDisposable
{
    sealed class FakeBin(string dir) : IRecycleBin
    {
        readonly Dictionary<string, string> _held = [];
        public void Recycle(string path)
        {
            var dest = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path)) Directory.Move(path, dest); else File.Move(path, dest);
            _held[path] = dest;
        }
        public bool Restore(string originalPath)
        {
            if (!_held.Remove(originalPath, out var p)) return false;
            if (Directory.Exists(p)) Directory.Move(p, originalPath); else File.Move(p, originalPath);
            return true;
        }
    }

    readonly string _root = Path.Combine(Path.GetTempPath(), "colo-files-" + Guid.NewGuid().ToString("N"));
    readonly string _dir;
    readonly FileOps _ops;

    public FileOpsTests()
    {
        _dir = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        _ops = new FileOps(new FakeBin(Path.Combine(_root, "bin")));
        foreach (var n in new[] { "a.pdf", "b.pdf", "foto.jpg" }) File.WriteAllText(Path.Combine(_dir, n), n);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string P(string n) => Path.Combine(_dir, n);

    [Fact]
    public void Rename_keeps_extension_and_undo_puts_it_back()
    {
        var r = _ops.Run("rename", _dir, ["a.pdf", "foto.jpg"], "", ["Contrato 2026", "Viaje.jpg"], "");
        Assert.StartsWith("ok: renombrado (2)", r);
        Assert.True(File.Exists(P("Contrato 2026.pdf")));
        Assert.True(File.Exists(P("Viaje.jpg")));
        Assert.StartsWith("ok: deshecho", _ops.Run("undo", null, [], "", [], ""));
        Assert.True(File.Exists(P("a.pdf")));
        Assert.True(File.Exists(P("foto.jpg")));
    }

    [Fact]
    public void Rename_never_overwrites()
    {
        var r = _ops.Run("rename", _dir, ["a.pdf"], "", ["b"], "");
        Assert.StartsWith("error: no hice nada", r);
        Assert.Contains("ya existe b.pdf", r);
        Assert.Equal("a.pdf", File.ReadAllText(P("a.pdf")));
    }

    [Fact]
    public void Move_with_wildcard_creates_folder_and_undo_removes_it()
    {
        var r = _ops.Run("move", _dir, ["*.pdf"], "Contratos", [], "");
        Assert.StartsWith("ok: movido (3)", r); // carpeta + 2 archivos
        Assert.True(File.Exists(P("Contratos/a.pdf")));
        Assert.False(File.Exists(P("a.pdf")));
        _ops.Run("undo", null, [], "", [], "");
        Assert.True(File.Exists(P("a.pdf")));
        Assert.True(File.Exists(P("b.pdf")));
        Assert.False(Directory.Exists(P("Contratos")));
    }

    [Fact]
    public void Copy_skips_existing_and_delete_goes_to_the_bin_and_comes_back()
    {
        Directory.CreateDirectory(P("Copia"));
        File.WriteAllText(P("Copia/a.pdf"), "otro");
        var r = _ops.Run("copy", _dir, ["a.pdf", "b.pdf"], P("Copia"), [], "");
        Assert.Contains("ya existe", r);
        Assert.Equal("otro", File.ReadAllText(P("Copia/a.pdf")));
        Assert.True(File.Exists(P("Copia/b.pdf")));

        Assert.StartsWith("ok: a la papelera (1)", _ops.Run("delete", _dir, ["foto.jpg"], "", [], ""));
        Assert.False(File.Exists(P("foto.jpg")));
        Assert.Contains("recuperé", _ops.Run("undo", null, [], "", [], ""));
        Assert.True(File.Exists(P("foto.jpg")));
    }

    [Fact]
    public void Zip_unzip_list_and_relative_paths_need_a_folder()
    {
        Assert.StartsWith("ok: comprimido", _ops.Run("zip", _dir, ["a.pdf", "b.pdf"], "pdfs", [], ""));
        Assert.True(File.Exists(P("pdfs.zip")));
        Assert.StartsWith("ok: descomprimido", _ops.Run("unzip", _dir, ["pdfs.zip"], "", [], ""));
        Assert.Equal("a.pdf", File.ReadAllText(P("pdfs/a.pdf")));
        var list = _ops.Run("list", _dir, [], "", [], "*.pdf");
        Assert.Contains("a.pdf", list);
        Assert.DoesNotContain("foto.jpg", list);
        Assert.StartsWith("error:", _ops.Run("rename", null, ["a.pdf"], "", ["x"], ""));
        Assert.StartsWith("error: no hay nada que deshacer", new FileOps(new FakeBin(_root)).Undo());
    }

    [Fact]
    public void Folder_cannot_go_inside_itself()
    {
        Directory.CreateDirectory(P("X"));
        var r = _ops.Run("move", _dir, ["X"], P("X/dentro"), [], "");
        Assert.Contains("dentro de sí misma", r);
        Assert.True(Directory.Exists(P("X")));
    }

    [Fact]
    public void Tilde_paths_reach_the_real_user_folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Downloads", "x.pdf"), FileOps.Resolve("~/Descargas/x.pdf", null));
        Assert.Equal(Path.Combine(home, "Downloads"), FileOps.Resolve("~/downloads", null).TrimEnd('/', '\\'));
        Assert.Equal(Path.Combine(home, "algo", "y"), FileOps.Resolve("~/algo/y", null));
        Assert.Equal(Path.Combine(_dir, "z.txt"), FileOps.Resolve("z.txt", _dir));
    }
}
