using System.Text;
using System.Text.Json.Nodes;

namespace Colo.Core;

public sealed record ModelInfo(string Id, string? BaseModel)
{
    public override string ToString() =>
        string.IsNullOrEmpty(BaseModel) || BaseModel == Id ? Id : $"{Id}  ·  {BaseModel}";
}

public sealed class AiException(string message) : Exception(message);

/// <summary>
/// OpenAI y Azure AI Foundry. En Azure el agente usa la API v1 (/openai/v1/responses)
/// con el nombre del deployment como modelo.
/// </summary>
public sealed class AiClient
{
    const string AzureTranscribeApiVersion = "2025-03-01-preview";
    static readonly string[] AzureListApiVersions = ["2022-12-01", "2023-05-15"];

    readonly Settings _s;
    readonly HttpClient _http;

    public AiClient(Settings settings, HttpClient? http = null)
    {
        _s = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>OpenAI: base de la API (por defecto api.openai.com/v1). Azure: https://host del recurso.</summary>
    public static string Root(Settings s)
    {
        var e = s.Endpoint.Trim();
        if (s.Provider == Provider.OpenAI)
            return string.IsNullOrEmpty(e) ? "https://api.openai.com/v1" : e.TrimEnd('/');
        if (!Uri.TryCreate(e, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw new AiException("Endpoint inválido. Ej.: https://mi-recurso.openai.azure.com/");
        return $"{u.Scheme}://{u.Authority}";
    }

    string V1 => _s.Provider == Provider.OpenAI ? Root(_s) : Root(_s) + "/openai/v1";

    HttpRequestMessage Req(HttpMethod method, string url)
    {
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) throw new AiException("Falta la API key.");
        var r = new HttpRequestMessage(method, url);
        if (_s.Provider == Provider.OpenAI)
            r.Headers.Authorization = new("Bearer", _s.ApiKey.Trim());
        else
            r.Headers.Add("api-key", _s.ApiKey.Trim());
        return r;
    }

    async Task<JsonNode> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await _http.SendAsync(req, ct))
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new AiException($"HTTP {(int)resp.StatusCode}: {ErrorText(body)}");
            return JsonNode.Parse(body) ?? throw new AiException("Respuesta vacía.");
        }
    }

    static string ErrorText(string body)
    {
        try
        {
            var msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(msg)) return msg;
        }
        catch { }
        body = body.Trim();
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        if (_s.Provider == Provider.OpenAI)
        {
            var j = await Send(Req(HttpMethod.Get, V1 + "/models"), ct);
            return j["data"]!.AsArray()
                .Select(m => new ModelInfo(m!["id"]!.GetValue<string>(), null))
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        AiException? last = null;
        foreach (var v in AzureListApiVersions)
        {
            try
            {
                var j = await Send(Req(HttpMethod.Get, $"{Root(_s)}/openai/deployments?api-version={v}"), ct);
                return j["data"]!.AsArray()
                    .Select(d => new ModelInfo(d!["id"]!.GetValue<string>(), d["model"]?.GetValue<string>()))
                    .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (AiException ex) { last = ex; }
        }
        throw last!;
    }

    public Task<JsonNode> RespondAsync(JsonObject body, CancellationToken ct)
    {
        var r = Req(HttpMethod.Post, V1 + "/responses");
        r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return Send(r, ct);
    }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.TranscribeModel)) throw new AiException("Elige el modelo de voz.");
        var url = _s.Provider == Provider.OpenAI
            ? V1 + "/audio/transcriptions"
            : $"{Root(_s)}/openai/deployments/{Uri.EscapeDataString(_s.TranscribeModel)}/audio/transcriptions?api-version={AzureTranscribeApiVersion}";
        var r = Req(HttpMethod.Post, url);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "voz.wav");
        form.Add(new StringContent(_s.TranscribeModel), "model");
        r.Content = form;
        var j = await Send(r, ct);
        return j["text"]?.GetValue<string>()?.Trim() ?? "";
    }
}
