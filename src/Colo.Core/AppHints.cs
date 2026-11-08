namespace Colo.Core;

/// <summary>
/// Cómo se maneja la app que está delante (atajos, qué herramienta usar). Va solo en el turno en que esa app
/// está activa: el prompt fijo sigue corto y cacheado.
/// </summary>
public static class AppHints
{
    /// <summary>Escribir un correo, igual en el Outlook (clásico, nuevo o web) y en Gmail.</summary>
    const string Compose = "Correo nuevo: ctrl+n (Gmail: c). Para/CC: type_text del nombre o correo, sin submit; en el turno siguiente click en la persona correcta de las sugerencias (nombre y correo) y que quede como contacto; varias, una a una. Asunto corto. Cuerpo: type_text con mark en el cuerpo, el correo entero (saludo, texto, despedida; la firma ya está). Adjuntar: find_files → clipboard set_files → clic en el cuerpo → ctrl+v; si no se adjunta, botón de adjuntar › examinar y la ruta entera en el nombre de archivo + enter. Responder: abre el correo y responder / responder a todos. Enviar (ctrl+enter) solo si lo pidió.";

    /// <summary>window = etiqueta «título  [programa]» de la ventana activa; url = la del navegador si es uno.</summary>
    public static string For(string app, string window, string url = "")
    {
        var title = window.EndsWith($"[{app}]", StringComparison.OrdinalIgnoreCase)
            ? window[..^(app.Length + 2)].TrimEnd() : window;
        var shell = Terminal.Kind(app, title);
        if (shell != Shell.None) return Terminal.Hint(shell);
        if (url.Length > 0 && Mail(url) is { } web) return web;
        return app.ToLowerInvariant() switch
        {
            "explorer" => "Explorador: files trabaja sobre la carpeta abierta (rutas relativas). f2 renombra, ctrl+shift+n carpeta nueva, alt+↑ sube, ctrl+l ruta, ctrl+e busca.",
            "winword" => "Word: doc antes que ctrl+f o desplazar (outline, goto, read, find, write, format). Preguntas sobre el documento: read (section o all) y responde. Escribir o completar una parte: read de esa sección y write con el tono y el estilo del documento (at=section_end para añadir al final).",
            "olk" or "outlook" => Outlook + "\n" + Compose,
            "excel" => "Excel: sheet para leer, escribir, fórmulas y filtros. f2 edita la celda, ctrl+flechas salta al borde de la tabla, ctrl+z deshace.",
            _ => "",
        };
    }

    const string Outlook = "Outlook: cada cuenta es un nodo plegable del panel de carpetas (sus carpetas salen «en «cuenta»»): click con expand. Revisar correos: en cada cuenta, su bandeja de entrada y read_text list (filas enteras). La búsqueda mira solo el buzón actual: para todas, elige todas las cuentas en el alcance junto a la caja de búsqueda. Un código o correo que acaba de llegar: búscalo en todas, ábrelo y read_text.";

    /// <summary>El correo en el navegador (Outlook web, Gmail): mismas reglas que la app.</summary>
    static string? Mail(string url)
    {
        if (!Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("outlook.") || (host.EndsWith("cloud.microsoft") && u.AbsolutePath.StartsWith("/mail")))
            return Outlook + "\n" + Compose;
        if (host == "mail.google.com") return "Gmail: carpetas y etiquetas en el panel izquierdo; read_text list da las filas enteras.\n" + Compose;
        return null;
    }
}
