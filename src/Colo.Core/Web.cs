using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Colo.Core;

public sealed record WebHit(string Title, string Url, string Snippet);
public sealed record WebVideo(string Id, string Title, string Length, string Channel, string Views);

/// <summary>
/// Internet sin abrir el navegador ni claves: buscar (Bing; DuckDuckGo si falla), leer una página como texto, bajar
/// un archivo, vídeos de YouTube e imágenes de Wikimedia Commons. Módulo aparte: si se quita, nada más cambia.
/// </summary>
public sealed class Web(HttpClient? http = null)
{
    public const int PageChars = 6000;
    const long MaxDownload = 500L << 20;

    static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,es;q=0.8,en;q=0.7");
        return h;
    });

    readonly HttpClient _http = http ?? Shared.Value;

    // windows-1252 y otras páginas de códigos (sin esto, «€» y las comillas “ ” de webs antiguas salían rotas).
    static Web() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>La herramienta web. downloads = carpeta donde bajar los archivos.</summary>
    public async Task<string> RunAsync(ToolCall c, string downloads, CancellationToken ct)
    {
        try
        {
            return c.Str("action") switch
            {
                "read" => await ReadAsync(c.Str("url"), c.Int("offset") ?? 0, ct),
                "download" => await DownloadAsync(c.Str("url"), downloads, c.Str("name"), ct),
                "videos" => Format(await VideosAsync(c.Str("query"), ct)),
                "images" => await ImagesAsync(c.Str("query"), ct),
                _ => Format(await SearchAsync(c.Str("query"), ct)),
            };
        }
        catch (HttpRequestException e) { return $"error: {(e.StatusCode is { } s ? $"HTTP {(int)s}" : e.Message)}"; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return "error: la página tardó demasiado"; }
        catch (ArgumentException e) { return "error: " + e.Message; }
        catch (IOException e) { return "error: " + e.Message; }
    }

    // ───────────────────────── buscar

    public async Task<List<WebHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (query.Trim().Length == 0) throw new ArgumentException("falta query");
        var hits = new List<WebHit>();
        try
        {
            var html = await _http.GetStringAsync("https://www.bing.com/search?setlang=pt-br&q=" + Uri.EscapeDataString(query), ct);
            hits = ParseBing(html);
        }
        catch (HttpRequestException) { }
        if (hits.Count > 0) return hits;
        using var form = new FormUrlEncodedContent([KeyValuePair.Create("q", query)]);
        using var r = await _http.PostAsync("https://html.duckduckgo.com/html/", form, ct);
        return ParseDuckDuckGo(await r.Content.ReadAsStringAsync(ct));
    }

    static string Format(List<WebHit> hits) => hits.Count == 0 ? "ok: nada (prueba otras palabras)"
        : "ok:\n" + string.Join("\n", hits.Take(8).Select((h, i) => $"{i + 1}. {h.Title} — {h.Url}" + (h.Snippet.Length > 0 ? $"\n   {h.Snippet}" : "")));

    static string Format(List<WebVideo> videos) => videos.Count == 0 ? "ok: nada"
        : "ok:\n" + string.Join("\n", videos.Take(8).Select((v, i) =>
            $"{i + 1}. {v.Title} · {v.Length} · {v.Channel}{(v.Views.Length > 0 ? " · " + v.Views : "")} — https://www.youtube.com/watch?v={v.Id}"));

    public static List<WebHit> ParseBing(string html)
    {
        var hits = new List<WebHit>();
        foreach (Match m in Regex.Matches(html, @"<li class=""b_algo""(.*?)</li>", RegexOptions.Singleline))
        {
            var a = Regex.Match(m.Groups[1].Value, @"<h2[^>]*>\s*<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
            if (!a.Success) continue;
            var p = Regex.Match(m.Groups[1].Value, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline);
            var url = BingTarget(WebUtility.HtmlDecode(a.Groups[1].Value));
            if (url is null) continue;
            hits.Add(new WebHit(Clean(a.Groups[2].Value), url, p.Success ? Clean(p.Groups[1].Value) : ""));
        }
        return hits;
    }

    /// <summary>Los enlaces de Bing pasan por /ck/a?…&amp;u=a1&lt;base64&gt;: el destino real.</summary>
    static string? BingTarget(string href)
    {
        if (!href.Contains("bing.com/ck/a", StringComparison.Ordinal)) return href.StartsWith("http") ? href : null;
        var u = Regex.Match(href, @"[?&]u=a1([^&]+)");
        if (!u.Success) return null;
        try
        {
            var b = u.Groups[1].Value.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b));
        }
        catch { return null; }
    }

    public static List<WebHit> ParseDuckDuckGo(string html)
    {
        var hits = new List<WebHit>();
        var snippets = Regex.Matches(html, @"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline).Select(m => Clean(m.Groups[1].Value)).ToList();
        var links = Regex.Matches(html, @"class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
        for (var i = 0; i < links.Count; i++)
        {
            var href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
            var uddg = Regex.Match(href, @"[?&]uddg=([^&]+)");
            if (uddg.Success) href = Uri.UnescapeDataString(uddg.Groups[1].Value);
            if (href.StartsWith("//")) href = "https:" + href;
            if (!href.StartsWith("http")) continue;
            hits.Add(new WebHit(Clean(links[i].Groups[2].Value), href, i < snippets.Count ? snippets[i] : ""));
        }
        return hits;
    }

    // ───────────────────────── leer

    /// <summary>La página como texto (sin menús ni scripts; con los datos estructurados JSON-LD), por trozos.</summary>
    public async Task<string> ReadAsync(string url, int offset, CancellationToken ct)
    {
        var uri = Url(url);
        using var r = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        var type = r.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("pdf") || IsBinary(type))
        {
            var kb = (r.Content.Headers.ContentLength ?? 0) / 1024;
            return $"ok: es un archivo {type}{(kb > 0 ? $" ({kb} KB)" : "")}, no una página: web download para guardarlo";
        }
        var body = Decode(await r.Content.ReadAsByteArrayAsync(ct), r.Content.Headers.ContentType?.CharSet);
        var text = type.Contains("html") || body.TrimStart().StartsWith('<') ? PageText(body) : body;
        return Slice($"{r.RequestMessage?.RequestUri ?? uri}\n{text}", offset);
    }

    public static string Slice(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
        var end = Math.Min(text.Length, offset + PageChars);
        var part = text[offset..end];
        return "ok:\n" + part + (end < text.Length ? $"\n…(sigue: offset={end} de {text.Length})" : "");
    }

    /// <summary>HTML → texto legible: título, JSON-LD, y el main/article si lo hay; títulos con #, listas con -.</summary>
    public static string PageText(string html)
    {
        var sb = new StringBuilder();
        var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (title.Success) sb.AppendLine("# " + Clean(title.Groups[1].Value));
        foreach (Match ld in Regex.Matches(html, @"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var json = ld.Groups[1].Value.Trim();
            if (json.Length is > 0 and < 4000) sb.AppendLine("Datos: " + Regex.Replace(json, @"\s+", " "));
        }
        var h = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        h = Regex.Replace(h, @"<(script|style|noscript|svg|template|iframe)\b.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var main = Regex.Matches(h, @"<(main|article)\b[^>]*>(.*?)</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups[2].Value).OrderByDescending(s => s.Length).FirstOrDefault();
        if (main is { Length: > 500 }) h = main;
        else h = Regex.Replace(h, @"<(nav|footer|header|aside|form)\b.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<h[1-4][^>]*>", "\n## ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<(td|th)[^>]*>", " | ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<(br|/p|/div|/tr|/h[1-6]|/ul|/ol|/table|/section|p|div|tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        h = WebUtility.HtmlDecode(Regex.Replace(h, @"<[^>]+>", " "));
        h = Regex.Replace(h, @"[ \t ]+", " ");
        h = Regex.Replace(h, @" *\n[ \n]*", "\n").Trim();
        sb.Append(h);
        return sb.ToString();
    }

    /// <summary>
    /// Texto con su codificación: la de la cabecera, o la del &lt;meta charset&gt; (sitios del gobierno en
    /// ISO-8859-1 salían con «Ã§»), o UTF-8.
    /// </summary>
    public static string Decode(byte[] bytes, string? charset)
    {
        static Encoding? Named(string? name)
        {
            name = name?.Trim().Trim('"', '\'').ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return null;
            // Las webs que dicen ISO-8859-1 en realidad escriben windows-1252 (como hacen los navegadores).
            if (name is "iso-8859-1" or "latin1" or "windows-1252" or "cp1252" or "us-ascii") name = "windows-1252";
            try { return Encoding.GetEncoding(name); } catch { return null; }
        }
        var enc = Named(charset);
        if (enc is null)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase);
            enc = m.Success ? Named(m.Groups[1].Value) : null;
        }
        return (enc ?? Encoding.UTF8).GetString(bytes);
    }

    // ───────────────────────── bajar

    public async Task<string> DownloadAsync(string url, string folder, string name, CancellationToken ct)
    {
        var uri = Url(url);
        using var r = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        if (r.Content.Headers.ContentLength > MaxDownload) return "error: el archivo pasa de 500 MB";
        var type = r.Content.Headers.ContentType?.MediaType ?? "";
        var file = FileName(name, r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName,
            r.RequestMessage?.RequestUri ?? uri, type);
        Directory.CreateDirectory(folder);
        var path = Unique(Path.Combine(folder, file));
        var tmp = path + ".part";
        try
        {
            await using (var src = await r.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                // Sin Content-Length el tope se mira mientras baja.
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    if (dst.Length + n > MaxDownload) throw new IOException("el archivo pasa de 500 MB");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (Mismatch(tmp, Path.GetExtension(path)) is { } why) { File.Delete(tmp); return "error: " + why; }
            File.Move(tmp, path);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
        return $"ok: {path} ({new FileInfo(path).Length / 1024} KB)";
    }

    /// <summary>
    /// Una web que pide login o pone una página intermedia devuelve HTML con el nombre del PDF: eso no es el archivo.
    /// </summary>
    public static string? Mismatch(string path, string ext)
    {
        var head = new byte[8];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
        if (n == 0) return "llegó vacío";
        var start = Encoding.ASCII.GetString(head, 0, n).TrimStart();
        var html = start.StartsWith('<');
        return ext.ToLowerInvariant() switch
        {
            ".pdf" when !start.StartsWith("%PDF") => html ? "la web devolvió una página, no el PDF (¿login o página intermedia? ábrela con browser)" : "no es un PDF",
            ".docx" or ".xlsx" or ".pptx" or ".zip" when !start.StartsWith("PK") => "la web devolvió una página, no el archivo (¿login?)",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".mp3" or ".mp4" when html => "la web devolvió una página, no el archivo",
            _ => null,
        };
    }

    public static string FileName(string wanted, string? disposition, Uri uri, string type)
    {
        var ext = type switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/zip" => ".zip",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/mpeg" => ".mp3",
            "video/mp4" => ".mp4",
            "text/csv" => ".csv",
            "text/plain" => ".txt",
            "text/html" => ".html",
            _ => "",
        };
        var fromUrl = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        var name = (wanted.Trim().Length > 0 ? wanted.Trim() : disposition?.Trim('"', ' ') is { Length: > 0 } d ? d : fromUrl).Trim();
        foreach (var bad in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])) name = name.Replace(bad, '_');
        if (name.Length == 0 || name.Trim('.', '_').Length == 0) name = uri.Host.Replace("www.", "") + ext;
        else if (Path.GetExtension(name).Length == 0) name += ext.Length > 0 ? ext : Path.GetExtension(fromUrl);
        return name.Length > 120 ? name[..100] + Path.GetExtension(name) : name;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    // ───────────────────────── vídeos e imágenes

    public async Task<List<WebVideo>> VideosAsync(string query, CancellationToken ct)
    {
        if (query.Trim().Length == 0) throw new ArgumentException("falta query");
        var html = await _http.GetStringAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query), ct);
        return ParseYouTube(html);
    }

    public static List<WebVideo> ParseYouTube(string html)
    {
        var m = Regex.Match(html, @"var ytInitialData\s*=\s*(\{.*?\});\s*</script>", RegexOptions.Singleline);
        var list = new List<WebVideo>();
        if (!m.Success) return list;
        JsonNode? root;
        try { root = JsonNode.Parse(m.Groups[1].Value); } catch (JsonException) { return list; }
        void Walk(JsonNode? n)
        {
            if (list.Count >= 12) return;
            if (n is JsonObject o)
            {
                if (o["videoRenderer"] is JsonObject v && v["videoId"]?.GetValue<string>() is { } id)
                    list.Add(new WebVideo(id, Runs(v["title"]), v["lengthText"]?["simpleText"]?.GetValue<string>() ?? "en vivo",
                        Runs(v["ownerText"]), v["viewCountText"]?["simpleText"]?.GetValue<string>() ?? ""));
                else foreach (var kv in o) Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var x in a) Walk(x);
        }
        Walk(root);
        return list;
    }

    static string Runs(JsonNode? n) => n?["simpleText"]?.GetValue<string>()
        ?? string.Concat((n?["runs"] as JsonArray ?? []).Select(r => r?["text"]?.GetValue<string>() ?? ""));

    public async Task<string> ImagesAsync(string query, CancellationToken ct)
    {
        if (query.Trim().Length == 0) throw new ArgumentException("falta query");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=10&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth=960&gsrsearch="
            + Uri.EscapeDataString(query + " filetype:bitmap"));
        // Wikimedia pide identificarse; con el agente de navegador devuelve error.
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd("Colo/1.18 (asistente de escritorio; https://github.com/Villamediana)");
        using var r = await _http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return FormatImages(await r.Content.ReadAsStringAsync(ct));
    }

    public static string FormatImages(string json)
    {
        var pages = JsonNode.Parse(json)?["query"]?["pages"] as JsonObject;
        if (pages is null || pages.Count == 0) return "ok: nada";
        var rows = pages.Select(kv => kv.Value!).OrderBy(p => p["index"]?.GetValue<int>() ?? 0).Select((p, i) =>
        {
            var info = p["imageinfo"]?[0];
            var thumb = info?["thumburl"]?.GetValue<string>() ?? info?["url"]?.GetValue<string>() ?? "";
            if (thumb.IndexOf('?') is var q and > 0) thumb = thumb[..q];
            var author = Clean(info?["extmetadata"]?["Artist"]?["value"]?.GetValue<string>() ?? "");
            var name = (p["title"]?.GetValue<string>() ?? "").Replace("File:", "");
            return $"{i + 1}. {Path.GetFileNameWithoutExtension(name)} — {thumb}{(author.Length > 0 ? $" (autor: {Trunc(author, 40)})" : "")}";
        });
        return "ok: imágenes de Wikimedia Commons\n" + string.Join("\n", rows);
    }

    // ───────────────────────── ayudas

    static Uri Url(string url)
    {
        url = url.Trim();
        if (url.Length == 0) throw new ArgumentException("falta url");
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url no válida");
        return uri;
    }

    static bool IsBinary(string type) => type.StartsWith("image/") || type.StartsWith("audio/") || type.StartsWith("video/")
        || type is "application/zip" or "application/octet-stream" || type.Contains("officedocument");

    static string Clean(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ").Trim();

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
