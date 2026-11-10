using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Colo.Core;

/// <summary>
/// Actualizar desde el último release del repo: baja el Colo.exe nuevo junto al que está corriendo, lo cambia
/// por el actual (Windows deja renombrar un .exe abierto, no sobrescribirlo) y el nuevo arranca y releva al viejo.
/// </summary>
public static class Updater
{
    public const string Repo = "Villamediana/Own-Computer-Use";
    public const string Asset = "Colo.exe";

    public sealed record Release(Version Version, string Tag, string Url, long Size);

    /// <summary>«v1.27.1» → 1.27.1 (siempre con tres números, para comparar con la versión del .exe).</summary>
    public static Version? VersionOf(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>El release (JSON de la API de GitHub) con su Colo.exe; null si no trae versión o el .exe.</summary>
    public static Release? Parse(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["tag_name"]?.GetValue<string>() is not { } tag || VersionOf(tag) is not { } version) return null;
        foreach (var a in root["assets"]?.AsArray() ?? [])
        {
            if (!string.Equals(a?["name"]?.GetValue<string>(), Asset, StringComparison.OrdinalIgnoreCase)) continue;
            var url = a!["browser_download_url"]?.GetValue<string>();
            var size = a["size"]?.GetValue<long>() ?? 0;
            if (string.IsNullOrEmpty(url) || size <= 0) return null;
            return new Release(version, tag, url, size);
        }
        return null;
    }

    public static bool IsNewer(Release r, Version current) =>
        r.Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    public static async Task<Release?> LatestAsync(HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Colo", "1"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GitHub respondió {(int)res.StatusCode}");
        return Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Baja el .exe a path (primero como .part). Solo queda si llegó entero (el tamaño del release) y es un
    /// ejecutable; si no, no deja nada.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, Release r, string path, Action<double>? progress, CancellationToken ct)
    {
        var part = path + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, r.Url);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Colo", "1"));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"la descarga respondió {(int)res.StatusCode}");
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[81920];
                long got = 0;
                var last = -1;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    var pct = (int)(got * 100 / r.Size);
                    if (pct != last) { last = pct; progress?.Invoke(Math.Min(1, (double)got / r.Size)); }
                }
            }
            var len = new FileInfo(part).Length;
            if (len != r.Size) throw new IOException($"llegó a medias ({len} de {r.Size} bytes)");
            if (!IsExe(part)) throw new IOException("lo descargado no es un programa");
            File.Move(part, path, true);
        }
        finally { try { File.Delete(part); } catch { } }
    }

    static bool IsExe(string path)
    {
        using var f = File.OpenRead(path);
        return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    /// <summary>El nombre que toma el .exe viejo mientras el nuevo ocupa su lugar.</summary>
    public static string OldPath(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".old.exe");

    /// <summary>
    /// El nuevo ocupa el lugar del actual (mismo nombre: los accesos, el arranque con Windows y el instalador
    /// siguen apuntando bien). Si algo falla, el actual vuelve a su sitio.
    /// </summary>
    public static void Swap(string exe, string fresh)
    {
        var old = OldPath(exe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>Al arrancar: borra el .exe viejo que dejó la actualización (si el anterior ya cerró).</summary>
    public static bool Cleanup(string exe)
    {
        var old = OldPath(exe);
        if (!File.Exists(old)) return true;
        try { File.Delete(old); return true; }
        catch { return false; }
    }
}
