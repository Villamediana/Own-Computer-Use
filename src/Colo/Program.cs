using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Colo.Core;
using Microsoft.Win32;

namespace Colo;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Viene de «Buscar actualización»: el anterior se está cerrando solo (guarda lo suyo); se le espera en vez
        // de relevarlo a la fuerza.
        if (args.Length == 2 && args[0] == "--after" && int.TryParse(args[1], out var pid)) WaitExit(pid);
        using var single = new Mutex(true, "Colo.SingleInstance", out var first);
        // Ya hay un Colo abierto (el que arranca con Windows, quizá de una versión anterior): este lo releva.
        // Antes salía callado y parecía que el .exe nuevo no abría.
        if (!first && !TakeOver(single)) return;
        Log.Write($"── Colo {Application.ProductVersion.Split('+')[0]} · Windows {Environment.OSVersion.Version} · {Environment.ProcessorCount} núcleos");
        AfterUpdate();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("ERROR sin capturar: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("ERROR en tarea: " + e.Exception); e.SetObserved(); };
        // Proxy de empresa que pide usuario: se usan las credenciales de Windows.
        try { System.Net.Http.HttpClient.DefaultProxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials; } catch { }
        WhisperNative.Prepare();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ERROR en la interfaz: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Colo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Application.Run(new Widget());
    }

    static void WaitExit(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(20_000);
        }
        catch { } // ya cerró
    }

    /// <summary>
    /// Tras actualizar: borra el .exe viejo que quedó al lado (en cuanto el anterior suelte el archivo) y pone la
    /// versión nueva en «Aplicaciones instaladas» si este es el Colo instalado.
    /// </summary>
    static void AfterUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        if (File.Exists(Updater.OldPath(exe)))
            Task.Run(async () =>
            {
                for (var i = 0; i < 15 && !Updater.Cleanup(exe); i++) await Task.Delay(1000);
            });
        try
        {
            var version = Application.ProductVersion.Split('+')[0];
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Colo", writable: true);
            if (k?.GetValue("InstallLocation") is not string loc) return;
            var here = Path.GetDirectoryName(exe)!.TrimEnd('\\');
            if (!string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase)) return;
            if (k.GetValue("DisplayVersion") as string != version) k.SetValue("DisplayVersion", version);
        }
        catch (Exception ex) { Log.Write("instalado: no pude poner la versión · " + ex.Message); }
    }

    /// <summary>Cierra el otro Colo (por las buenas y, si no contesta, a la fuerza) y se queda con el candado.</summary>
    static bool TakeOver(Mutex single)
    {
        var me = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Colo"))
            using (p)
            {
                if (p.Id == me) continue;
                var old = "?";
                try { old = p.MainModule?.FileVersionInfo.ProductVersion ?? "?"; } catch { }
                try
                {
                    if (!p.CloseMainWindow() || !p.WaitForExit(2500))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    Log.Write($"relevo: cerré el Colo anterior ({old}, pid {p.Id})");
                }
                catch (Exception ex) { Log.Write($"relevo: no pude cerrar el Colo anterior (pid {p.Id}) · {ex.Message}"); }
            }
        try { return single.WaitOne(5000); }
        catch (AbandonedMutexException) { return true; } // el otro murió con el candado: ya es nuestro
    }
}
