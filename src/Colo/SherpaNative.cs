using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Colo;

/// <summary>
/// Las DLL de sherpa-onnx (quién habla en las reuniones) van dentro del .exe, como las de Whisper: se extraen la
/// primera vez que hacen falta a %LOCALAPPDATA%\Colo\native\sherpa y se cargan desde ahí (onnxruntime primero, para
/// que no se use otro onnxruntime.dll que Windows tenga en System32).
/// </summary>
static class SherpaNative
{
    static readonly string[] Order = ["onnxruntime.dll", "sherpa-onnx-c-api.dll"];
    static readonly object Gate = new();
    static bool _ready;

    public static void Prepare()
    {
        lock (Gate)
        {
            if (_ready) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "native", "sherpa");
            Directory.CreateDirectory(dir);
            var asm = typeof(SherpaNative).Assembly;
            foreach (var name in Order)
            {
                using var res = asm.GetManifestResourceStream("sherpa/" + name) ?? throw new FileNotFoundException("falta el recurso " + name);
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && new FileInfo(path).Length == res.Length) continue;
                using var file = File.Create(path);
                res.CopyTo(file);
            }
            NativeLibrary.Load(Path.Combine(dir, Order[0]));
            var api = NativeLibrary.Load(Path.Combine(dir, Order[1]));
            NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OfflineSpeakerDiarization).Assembly,
                (name, _, _) => name.StartsWith("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase) ? api : IntPtr.Zero);
            _ready = true;
            Log.Write("voces: librerías listas en " + dir);
        }
    }
}
