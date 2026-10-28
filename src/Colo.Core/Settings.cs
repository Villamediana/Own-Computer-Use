namespace Colo.Core;

public enum Provider { Azure, OpenAI }

/// <summary>Local = Parakeet en el propio PC (gratis). Api = modelo de transcripción del proveedor.</summary>
public enum SpeechEngine { Local, Api }

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
    /// <summary>"" = no se envía (modelos sin razonamiento).</summary>
    public string ReasoningEffort { get; set; } = "low";
    public int Sensitivity { get; set; } = 6;
    public int MicDevice { get; set; }
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
