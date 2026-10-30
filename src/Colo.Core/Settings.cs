namespace Colo.Core;

public enum Provider { Azure, OpenAI }

/// <summary>
/// Local = Whisper en el propio PC (gratis). Api = modelo de transcripción del proveedor (OpenAI / Foundry).
/// ElevenLabs = Scribe por la API de ElevenLabs.
/// </summary>
public enum SpeechEngine { Local, Api, ElevenLabs }

/// <summary>Voz con que se leen las respuestas: la de Windows o una de ElevenLabs.</summary>
public enum ReplyVoice { Windows, ElevenLabs }

/// <summary>Auto = solo la lista de elementos (texto) y la captura cuando el modelo la pide. Always = captura en cada paso.</summary>
public enum Vision { Auto, Always }

public sealed class Settings
{
    public Provider Provider { get; set; } = Provider.Azure;
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public SpeechEngine SpeechEngine { get; set; } = SpeechEngine.Local;
    /// <summary>Modelo de Whisper local: «base» o «small».</summary>
    public string LocalModel { get; set; } = "base";
    /// <summary>Idioma de Whisper local: «es», «pt», «en» o «auto». La API, ElevenLabs y GPT-Live entienden el que hables.</summary>
    public string Language { get; set; } = "es";
    public string TranscribeModel { get; set; } = "";
    /// <summary>
    /// Conexión propia para la voz (transcripción por API y GPT-Live). false = la misma del modelo.
    /// </summary>
    public bool VoiceOwn { get; set; }
    public Provider VoiceProvider { get; set; } = Provider.Azure;
    public string VoiceEndpoint { get; set; } = "";
    public string VoiceKey { get; set; } = "";
    /// <summary>Conversación en vivo con GPT-Live: habla, delega las tareas en Colo y responde con su voz.</summary>
    public bool Live { get; set; }
    /// <summary>Deployment (Foundry) o modelo (OpenAI) de GPT-Live.</summary>
    public string LiveModel { get; set; } = "gpt-live-1";
    public string LiveVoice { get; set; } = "marin";
    public string ElevenLabsKey { get; set; } = "";
    public string ElevenLabsModel { get; set; } = "scribe_v2";
    /// <summary>"" = no se envía (modelos sin razonamiento).</summary>
    public string ReasoningEffort { get; set; } = "low";
    public int Sensitivity { get; set; } = 6;
    /// <summary>ID del micrófono en Windows (WASAPI); vacío = el predeterminado.</summary>
    public string MicId { get; set; } = "";
    /// <summary>Teclas que se mantienen apretadas para hablarle (al soltar, transcribe y actúa).</summary>
    public string TalkHotkey { get; set; } = "Ctrl+Win";
    /// <summary>Atajo para dictar: transcribe, el modelo lo pule con tu tono y lo escribe donde está el cursor. Vacío = apagado.</summary>
    public string DictateHotkey { get; set; } = "";
    public bool SpeakReplies { get; set; }
    public ReplyVoice ReplyVoice { get; set; } = ReplyVoice.ElevenLabs;
    public string ElevenVoiceId { get; set; } = "";
    public string ElevenVoiceName { get; set; } = "";
    /// <summary>eleven_v4 interpreta [etiquetas] de emoción; turbo y flash, más rápidos.</summary>
    public string ElevenTtsModel { get; set; } = "eleven_v4";
    public bool StartWithWindows { get; set; } = true;
    public Vision Vision { get; set; } = Vision.Auto;
    /// <summary>Grupos de herramientas propias apagados (ver <see cref="Tools.Groups"/>).</summary>
    public List<string> DisabledTools { get; set; } = [];
    public List<McpServerConfig> McpServers { get; set; } = [];
    /// <summary>Posición de la onda en pantalla (null = abajo al centro).</summary>
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
}
