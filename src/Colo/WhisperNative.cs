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

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "native", "whisper-1.9.1");

    public static void Prepare()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var asm = typeof(WhisperNative).Assembly;
            foreach (var name in Order)
            {
                using var res = asm.GetManifestResourceStream("whisper/" + name)
                    ?? throw new FileNotFoundException("falta el recurso " + name);
                var path = Path.Combine(Folder, name);
                if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
                {
                    using var file = File.Create(path);
                    res.CopyTo(file);
                }
            }
            foreach (var name in Order) NativeLibrary.Load(Path.Combine(Folder, name));
            LocalStt.UseNativeFolder(Folder);
            Log.Write("whisper: librerías listas en " + Folder);
        }
        catch (Exception ex) { Log.Write("whisper: no pude preparar las librerías · " + ex); }
    }
}
