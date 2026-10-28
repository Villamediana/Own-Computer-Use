using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Colo.Core;

public sealed record ElevenVoice(string Id, string Name, string About);

/// <summary>
/// Voz de ElevenLabs para las respuestas: GET /v1/voices y POST /v1/text-to-speech/{voz} (cabecera xi-api-key),
/// las mismas llamadas que usa la Luna. En v3/v4 la emoción va en etiquetas de audio dentro del texto
/// ([laughs], [sighs]…), que la voz interpreta en vez de leer; por eso no se manda style ni speaker boost.
/// </summary>
public sealed class ElevenLabsTts(HttpClient? http = null)
{
    const string Base = "https://api.elevenlabs.io/v1";
    /// <summary>PCM 16 bits mono sin cabecera: se reproduce sin decodificar mp3.</summary>
    public const int Rate = 24000;
    public static readonly string[] Models = ["eleven_v4", "eleven_v4_turbo", "eleven_flash_v2_5"];

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Modelos que interpretan [etiquetas]; los demás las leerían en voz alta.</summary>
    public static bool ActsTags(string? model) => Regex.IsMatch(model ?? "", "^eleven_(v[34]|flash_v2_5)");

    public async Task<List<ElevenVoice>> VoicesAsync(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/voices");
        req.Headers.Add("xi-api-key", key.Trim());
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException(Error(resp, body));
        var list = new List<ElevenVoice>();
        foreach (var v in JsonNode.Parse(body)?["voices"]?.AsArray() ?? [])
        {
            var id = v?["voice_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var labels = (v!["labels"] as JsonObject)?.Select(p => p.Value?.ToString() ?? "").Where(x => x.Length > 0).Take(3) ?? [];
            list.Add(new ElevenVoice(id, v["name"]?.GetValue<string>() ?? id, string.Join(" · ", labels)));
        }
        return list;
    }

    /// <summary>
    /// La frase como PCM 16 bits mono a <see cref="Rate"/> Hz, a trozos mientras se genera (/stream): se puede
    /// empezar a sonar con el primero. Quien llama libera la respuesta.
    /// </summary>
    public async Task<HttpResponseMessage> SpeechStreamAsync(string key, string voiceId, string model, string text, CancellationToken ct)
    {
        if (!ActsTags(model)) text = Expressive.Clean(text);
        var settings = new JsonObject { ["stability"] = 0.5, ["similarity_boost"] = 0.8 };
        if (!ActsTags(model))
        {
            settings["style"] = 0.2;
            settings["use_speaker_boost"] = true;
        }
        var json = new JsonObject { ["text"] = text, ["model_id"] = model, ["voice_settings"] = settings };
        var url = $"{Base}/text-to-speech/{Uri.EscapeDataString(voiceId.Trim())}/stream?output_format=pcm_{Rate}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("xi-api-key", key.Trim());
        req.Headers.Add("Accept", "audio/pcm");
        req.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.IsSuccessStatusCode) return resp;
        using (resp) throw new AiException(Error(resp, await resp.Content.ReadAsStringAsync(ct)));
    }

    static string Error(HttpResponseMessage resp, string body)
    {
        string? msg = null;
        try
        {
            var d = JsonNode.Parse(body)?["detail"];
            msg = d is JsonArray a ? a[0]?["msg"]?.GetValue<string>() : d?["message"]?.GetValue<string>() ?? d?.ToString();
        }
        catch { }
        return $"ElevenLabs {(int)resp.StatusCode}: {msg ?? (body.Length > 200 ? body[..200] : body)}";
    }
}

/// <summary>Respuestas con etiquetas de emoción para la voz; en pantalla y en la voz de Windows van sin ellas.</summary>
public static class Expressive
{
    /// <summary>Se suma a las instrucciones solo cuando la respuesta la dirá una voz v3/v4.</summary>
    public const string Note = """

Voz
- Tu done se dice en voz alta con una voz expresiva. Pon etiquetas de audio en inglés entre corchetes justo
  antes de las palabras que colorean: [warmly], [cheerfully], [laughs], [chuckles], [sighs], [curious],
  [excited], [softly], [playfully], [relieved]… Se interpretan, no se leen, y no se ven en pantalla.
  Una o dos por frase, con naturalidad, según lo que pasó. Sigue siendo una frase corta.
""";

    public static string Clean(string text) =>
        Regex.Replace(Regex.Replace(Regex.Replace(text, @"\[[^\]\n]{1,40}\]", " "), @"\s+([,.!?…;:])", "$1"), @"\s+", " ").Trim();
}
