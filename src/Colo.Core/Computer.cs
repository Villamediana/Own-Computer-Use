using System.Text.Json.Nodes;

namespace Colo.Core;

/// <summary>Elemento interactivo marcado con número en la captura. State: «foco», «activa», «marcada»… (vacío = nada).</summary>
public sealed record Mark(int Id, string Role, string Name, string Value = "", string State = "");

/// <summary>
/// Jpeg vacío = este turno va sin captura (solo la lista de elementos).
/// Others: las demás ventanas abiertas, para cambiar a una sin tener que listarlas antes.
/// App: programa de la ventana activa (chrome, winword…). Details: datos vivos de esa app (URL, carpeta,
/// seleccionados…), una línea cada uno.
/// </summary>
public sealed record Observation(byte[] Jpeg, int Width, int Height, string Window, IReadOnlyList<Mark> Marks,
    IReadOnlyList<string>? Others = null, string App = "", IReadOnlyList<string>? Details = null);

public sealed record ToolCall(string Name, JsonObject Args)
{
    /// <summary>Marca elegida; las marcas empiezan en 1, así que 0 o negativo es «sin marca».</summary>
    public int? Mark(string key = "mark") => Int(key) is int n && n > 0 ? n : null;
    public int? Int(string key) => Args[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : null;
    public double? Num(string key) => Args[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    public string Str(string key) => Args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public bool Bool(string key) => Args[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>Ve la pantalla y ejecuta acciones. La implementación real vive en el proyecto de Windows.</summary>
public interface IComputer
{
    Task<Observation> ObserveAsync(bool withImage, CancellationToken ct);
    /// <summary>Devuelve "ok" o "error: …" para que el modelo lo vea.</summary>
    Task<string> ExecuteAsync(ToolCall call, CancellationToken ct);
}

/// <summary>Herramientas de fuera (servidores MCP).</summary>
public interface IToolHost
{
    IReadOnlyList<JsonObject> Definitions { get; }
    bool Handles(string name);
    Task<string> CallAsync(string name, JsonObject args, CancellationToken ct);
}
