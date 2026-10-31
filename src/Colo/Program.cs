using System;
using System.Threading;
using System.Windows.Forms;

namespace Colo;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "Colo.SingleInstance", out var first);
        // Ya hay un Colo abierto (el que arranca con Windows, quizá de una versión anterior): este lo releva.
        // Antes salía callado y parecía que el .exe nuevo no abría.
        if (!first && !TakeOver(single)) return;
        Log.Write($"── Colo {Application.ProductVersion.Split('+')[0]} · Windows {Environment.OSVersion.Version} · {Environment.ProcessorCount} núcleos");
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
