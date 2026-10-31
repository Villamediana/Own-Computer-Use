using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Colo.Core;
using Microsoft.VisualBasic.FileIO;

namespace Colo;

/// <summary>Papelera de Windows: borrar con el Explorador y recuperar con «Restaurar» (verbo undelete).</summary>
sealed class WindowsRecycleBin : IRecycleBin
{
    public void Recycle(string path)
    {
        // En red (o en un pendrive) Windows no tiene papelera: borraría para siempre.
        var root = Path.GetPathRoot(path) ?? "";
        if (root.StartsWith(@"\\") || new DriveInfo(root).DriveType is not (DriveType.Fixed))
            throw new IOException($"{path} no está en un disco del PC (sin papelera): no lo borro; que lo borre él a mano");
        try
        {
            if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException)
        {
            // El diálogo de error de Windows (archivo en uso…) cancelado no es que el usuario pare a Colo.
            throw new IOException($"Windows no pudo mandar {Path.GetFileName(path)} a la papelera (¿abierto en otro programa?)");
        }
    }

    public bool Restore(string originalPath) => Desktop.Sta(() =>
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic bin = shell.NameSpace(10); // ssfBITBUCKET
        dynamic? best = null;
        var bestDate = DateTime.MinValue;
        foreach (var item in bin.Items())
        {
            string from = item.ExtendedProperty("System.Recycle.DeletedFrom") ?? "";
            if (!string.Equals(from.TrimEnd('\\'), Path.GetDirectoryName(originalPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            // El nombre visible viene sin extensión si el Explorador las oculta.
            string name = item.Name;
            if (!name.Equals(Path.GetFileName(originalPath), StringComparison.OrdinalIgnoreCase)
                && !name.Equals(Path.GetFileNameWithoutExtension(originalPath), StringComparison.OrdinalIgnoreCase)) continue;
            DateTime date = item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d ? d : DateTime.MinValue;
            if (best is null || date >= bestDate) { best = item; bestDate = date; }
        }
        if (best is null) return false;
        best.InvokeVerb("undelete");
        for (var i = 0; i < 20 && !File.Exists(originalPath) && !Directory.Exists(originalPath); i++) Thread.Sleep(50);
        return File.Exists(originalPath) || Directory.Exists(originalPath);
    });
}

/// <summary>Explorador de archivos: carpeta abierta y seleccionados de la ventana activa, búsqueda por contenido y abrir con.</summary>
static class Files
{
    /// <summary>
    /// Carpeta y seleccionados del Explorador que está delante (null si delante no hay Explorador). Con pestañas
    /// (Windows 11) todas comparten ventana: vale la que tiene el mismo nombre que el título.
    /// </summary>
    public static (string Folder, List<string> Selected)? Explorer(IntPtr hwnd)
    {
        if (!Native.ProcessName(hwnd).Equals("explorer", StringComparison.OrdinalIgnoreCase)) return null;
        var title = Native.Title(hwnd);
        var work = Task.Run(() => Desktop.Sta<(string, List<string>)?>(() =>
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            (string, List<string>)? first = null;
            foreach (var w in shell.Windows())
            {
                try
                {
                    if ((long)w.HWND != hwnd.ToInt64()) continue;
                    string folder = w.Document.Folder.Self.Path;
                    var selected = new List<string>();
                    foreach (var item in w.Document.SelectedItems())
                    {
                        selected.Add((string)item.Path);
                        if (selected.Count >= 200) break;
                    }
                    var found = (folder, selected);
                    string name = w.LocationName ?? "";
                    if (name.Length > 0 && title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return found;
                    first ??= found;
                }
                catch { }
            }
            return first;
        }));
        return work.Wait(1200) ? work.Result : null;
    }

    /// <summary>Líneas para el turno: carpeta y lo seleccionado (así «esto» y las rutas relativas tienen sentido).</summary>
    public static List<string> Details((string Folder, List<string> Selected) ex)
    {
        var lines = new List<string> { "Carpeta abierta: " + ex.Folder };
        if (ex.Selected.Count > 0)
        {
            var names = ex.Selected.Take(25).Select(p => string.Equals(Path.GetDirectoryName(p), ex.Folder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(p) : p);
            lines.Add($"Seleccionados ({ex.Selected.Count}): " + string.Join(" · ", names) + (ex.Selected.Count > 25 ? " …" : ""));
        }
        return lines;
    }

    /// <summary>
    /// Archivos que contienen esas palabras, por el índice de Windows Search (el mismo del buscador del Explorador).
    /// Va por ADO, que ya está en Windows: sin paquetes extra.
    /// </summary>
    public static string SearchContent(string query, CancellationToken ct)
    {
        var words = query.Replace("'", " ").Replace("\"", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "error: query vacío";
        var scope = new Uri(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).AbsoluteUri;
        var contains = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
        var sql = "SELECT TOP 15 System.ItemPathDisplay, System.DateModified FROM SystemIndex " +
                  $"WHERE SCOPE='{scope}' AND CONTAINS(*, '{contains}') ORDER BY System.DateModified DESC";
        var work = Task.Run(() => Desktop.Sta(() =>
        {
            dynamic conn = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection")!)!;
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            try
            {
                dynamic rs = conn.Execute(sql);
                var hits = new List<string>();
                while (!(bool)rs.EOF)
                {
                    string path = rs.Fields.Item("System.ItemPathDisplay").Value?.ToString() ?? "";
                    var date = rs.Fields.Item("System.DateModified").Value is DateTime d ? $" ({d:dd/MM/yyyy})" : "";
                    if (path.Length > 0) hits.Add(path + date);
                    rs.MoveNext();
                }
                rs.Close();
                return hits;
            }
            finally { conn.Close(); }
        }), ct);
        try
        {
            if (!work.Wait(8000, ct)) return "error: la búsqueda de Windows tardó demasiado";
        }
        catch (AggregateException ex)
        {
            return "error: no pude usar la búsqueda de Windows (" + ex.InnerException?.Message + "); busca por nombre";
        }
        var found = work.Result;
        return found.Count == 0 ? $"ok: ningún archivo indexado contiene «{query}»" : "ok:\n" + string.Join("\n", found);
    }

    static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["word"] = "winword.exe", ["excel"] = "excel.exe", ["powerpoint"] = "powerpnt.exe", ["outlook"] = "outlook.exe",
        ["bloc de notas"] = "notepad.exe", ["notepad"] = "notepad.exe", ["bloco de notas"] = "notepad.exe",
        ["vscode"] = "code", ["vs code"] = "code", ["visual studio code"] = "code", ["paint"] = "mspaint.exe",
        ["chrome"] = "chrome.exe", ["edge"] = "msedge.exe", ["firefox"] = "firefox.exe", ["acrobat"] = "acrobat.exe",
        ["notepad++"] = "notepad++.exe", ["fotos"] = "ms-photos:", ["photos"] = "ms-photos:",
    };

    public static string OpenWith(string path, string app)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return $"error: no existe {path}";
        app = app.Trim();
        if (app.Length == 0) return "error: falta app";
        var exe = Apps.TryGetValue(app, out var known) ? known : app;
        try
        {
            if (exe.EndsWith(':'))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            return "ok";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"error: no encontré el programa «{app}» (prueba el nombre del .exe)";
        }
    }
}
