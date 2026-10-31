namespace Colo.Core;

/// <summary>
/// Cómo se maneja la app que está delante (atajos, qué herramienta usar). Va solo en el turno en que esa app
/// está activa: el prompt fijo sigue corto y cacheado.
/// </summary>
public static class AppHints
{
    /// <summary>window = etiqueta «título  [programa]» de la ventana activa.</summary>
    public static string For(string app, string window)
    {
        var title = window.EndsWith($"[{app}]", StringComparison.OrdinalIgnoreCase)
            ? window[..^(app.Length + 2)].TrimEnd() : window;
        var shell = Terminal.Kind(app, title);
        if (shell != Shell.None) return Terminal.Hint(shell);
        return app.ToLowerInvariant() switch
        {
            "explorer" => "Explorador: files trabaja sobre la carpeta abierta (rutas relativas). f2 renombra, ctrl+shift+n carpeta nueva, alt+↑ sube, ctrl+l ruta, ctrl+e busca.",
            _ => "",
        };
    }
}
