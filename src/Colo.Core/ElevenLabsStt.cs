using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Colo.Core;

/// <summary>Transcripción con ElevenLabs Scribe: POST /v1/speech-to-text (multipart, cabecera xi-api-key).</summary>
public sealed class ElevenLabsStt(Settings settings, HttpClient? http = null)
{
    public const string Url = "https://api.elevenlabs.io/v1/speech-to-text";
    public static readonly string[] Models = ["scribe_v2", "scribe_v1"];

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ElevenLabsKey)) throw new AiException("Falta la API key de ElevenLabs.");
        using var req = new HttpRequestMessage(HttpMethod.Post, Url);
        req.Headers.Add("xi-api-key", settings.ElevenLabsKey.Trim());
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "voz.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(settings.ElevenLabsModel) ? Models[0] : settings.ElevenLabsModel.Trim()), "model_id");
        // Solo el texto: sin etiquetas de sonidos ni marcas de tiempo por palabra (más rápido).
        form.Add(new StringContent("false"), "tag_audio_events");
        form.Add(new StringContent("none"), "timestamps_granularity");
        if (settings.Language is { Length: > 0 } lang && lang != "auto") form.Add(new StringContent(lang), "language_code");
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string? msg = null;
            try
            {
                var j = JsonNode.Parse(body);
                msg = j?["detail"] is JsonArray a ? a[0]?["msg"]?.GetValue<string>()
                    : j?["detail"]?["message"]?.GetValue<string>() ?? j?["detail"]?.ToString();
            }
            catch { }
            throw new AiException($"ElevenLabs {(int)resp.StatusCode}: {msg ?? (body.Length > 200 ? body[..200] : body)}");
        }
        return JsonNode.Parse(body)?["text"]?.GetValue<string>()?.Trim() ?? "";
    }
}
