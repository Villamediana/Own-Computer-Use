namespace Colo.Core;

public enum Provider { Azure, OpenAI }

/// <summary>
/// Local = Parakeet en el propio PC (gratis). Api = modelo de transcripción del proveedor (OpenAI / Foundry).
/// ElevenLabs = Scribe por la API de ElevenLabs.
/// </summary>
public enum SpeechEngine { Local, Api, ElevenLabs }

/// <summary>Auto = solo la lista de elementos (texto) y la captura cuando el modelo la pide. Always = captura en cada paso.</summary>
public enum Vision { Auto, Always }

public sealed class Settings
{
    public Provider Provider { get; set; } = Provider.Azure;
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public SpeechEngine SpeechEngine { get; set; } = SpeechEngine.Local;
    public string TranscribeModel { get; set; } = "";
    public string ElevenLabsKey { get; set; } = "";
    public string ElevenLabsModel { get; set; } = "scribe_v2";
    /// <summary>"" = no se envía (modelos sin razonamiento).</summary>
    public string ReasoningEffort { get; set; } = "low";
    public int Sensitivity { get; set; } = 6;
    /// <summary>ID del micrófono en Windows (WASAPI); vacío = el predeterminado.</summary>
    public string MicId { get; set; } = "";
    public bool SpeakReplies { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public Vision Vision { get; set; } = Vision.Auto;
    /// <summary>Grupos de herramientas propias apagados (ver <see cref="Tools.Groups"/>).</summary>
    public List<string> DisabledTools { get; set; } = [];
    public List<McpServerConfig> McpServers { get; set; } = [];
    /// <summary>Posición de la onda en pantalla (null = abajo al centro).</summary>
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
}
