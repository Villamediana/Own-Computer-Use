using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Colo;

static class NativeLibs
{
    /// <summary>
    /// Windows 11 trae su propio onnxruntime.dll (más viejo) en System32. Si el de Colo se carga primero,
    /// la voz local usa siempre el correcto.
    /// </summary>
    public static void PreferBundledOnnxRuntime()
    {
        try
        {
            var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in dirs)
            {
                var path = Path.Combine(d, "onnxruntime.dll");
                if (!File.Exists(path)) continue;
                NativeLibrary.Load(path);
                Log.Write("onnxruntime: " + path);
                return;
            }
            Log.Write("onnxruntime: no encontrado junto a Colo (" + string.Join(" | ", dirs) + ")");
        }
        catch (Exception ex) { Log.Write("onnxruntime: no se pudo precargar · " + ex.Message); }
    }
}
