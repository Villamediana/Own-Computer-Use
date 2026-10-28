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
        if (!first) return;
        Log.Write($"── Colo {Application.ProductVersion.Split('+')[0]} · Windows {Environment.OSVersion.Version} · {Environment.ProcessorCount} núcleos");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("ERROR sin capturar: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("ERROR en tarea: " + e.Exception); e.SetObserved(); };
        // Proxy de empresa que pide usuario: se usan las credenciales de Windows.
        try { System.Net.Http.HttpClient.DefaultProxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials; } catch { }
        NativeLibs.PreferBundledOnnxRuntime();
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
}
