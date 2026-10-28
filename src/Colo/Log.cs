using System;
using System.IO;
using System.Text;

namespace Colo;

/// <summary>Registro en %LOCALAPPDATA%\Colo\colo.log para saber en qué paso se quedó algo.</summary>
static class Log
{
    static readonly object Gate = new();
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colo", "colo.log");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
