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
            "winword" => "Word: doc antes que ctrl+f o desplazar (outline, goto, read, find, write, format). Preguntas sobre el documento: read (section o all) y responde. Escribir o completar una parte: read de esa sección y write con el tono y el estilo del documento (at=section_end para añadir al final).",
            "olk" or "outlook" => "Outlook: puede tener varias cuentas, cada una plegada en el panel de la izquierda (clic en el nombre de la cuenta la despliega). La búsqueda mira solo el buzón actual: para todas, en el desplegable de alcance junto a la caja de búsqueda elige «Todas as caixas de correio». Un código o correo que acaba de llegar: búscalo en todas las cuentas, ábrelo y léelo con read_text.",
            "excel" => "Excel: sheet para leer, escribir, fórmulas y filtros. f2 edita la celda, ctrl+flechas salta al borde de la tabla, ctrl+z deshace.",
            _ => "",
        };
    }
}
