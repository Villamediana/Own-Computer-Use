using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Colo.Core;

namespace Colo;

/// <summary>Las DLL de Whisper van dentro del .exe; aquí se extraen una vez y se cargan desde ahí.</summary>
static class WhisperNative
{
    // Orden de dependencias: whisper.dll necesita las ggml.
    static readonly string[] Order = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];

    /// <summary>
    /// Whisper.net busca sus DLL en «runtimes\win-x64» junto al programa (así pasó la prueba en Windows real).
    /// Si esa carpeta no se puede escribir, se usa %LOCALAPPDATA%\Colo\native.
    /// </summary>
    public static string Folder { get; private set; } = "";

    public static void Prepare()
    {
        foreach (var root in new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "native"),
        })
        {
            try
            {
                var dir = Path.Combine(root, "runtimes", "win-x64");
                Extract(dir);
                Folder = dir;
                foreach (var name in Order) NativeLibrary.Load(Path.Combine(dir, name));
                LocalStt.UseNativeFolder(dir);
                Log.Write("whisper: librerías listas en " + dir);
                return;
            }
            catch (Exception ex) { Log.Write($"whisper: no pude usar {root} · {ex.Message}"); }
        }
    }

    static void Extract(string dir)
    {
        Directory.CreateDirectory(dir);
        var asm = typeof(WhisperNative).Assembly;
        foreach (var name in Order)
        {
            using var res = asm.GetManifestResourceStream("whisper/" + name)
                ?? throw new FileNotFoundException("falta el recurso " + name);
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length == res.Length) continue;
            using var file = File.Create(path);
            res.CopyTo(file);
        }
    }
}
