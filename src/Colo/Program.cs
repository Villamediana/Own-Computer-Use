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
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Colo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new Widget());
    }
}
