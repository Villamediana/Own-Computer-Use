using System.Text.Json.Nodes;

namespace Colo.Core;

/// <summary>Elemento interactivo marcado con número en la captura.</summary>
public sealed record Mark(int Id, string Role, string Name, string Value = "");

/// <summary>Jpeg vacío = este turno va sin captura (solo la lista de elementos).</summary>
public sealed record Observation(byte[] Jpeg, int Width, int Height, string Window, IReadOnlyList<Mark> Marks);

public sealed record ToolCall(string Name, JsonObject Args)
{
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
