using System.Text.Json.Nodes;

namespace Colo.Core;

public sealed record ToolGroup(string Id, string Title, string Summary, string[] Tools);

/// <summary>Instrucciones del modelo y herramientas propias, agrupadas para poder apagarlas en Ajustes.</summary>
public static class Tools
{
    public const string System = """
Eres Colo y manejas un PC con Windows por voz para alguien que tiene un bebé en brazos. El micrófono está
siempre abierto: te dicta órdenes frase a frase y las ejecutas según llegan.
Cada turno trae lo que dijo, lo que ya hiciste, la ventana activa y los elementos de la pantalla con número
([n] tipo «nombre» =valor). A veces también la captura, con esos números dibujados.

- Rápido y sin explicar. Encadena en una respuesta todo lo que no necesite ver el resultado
  (p. ej. type_text con mark + submit). Tras algo que cambia la pantalla, termina y espera el siguiente turno.
- Apunta con mark. x,y solo si tienes la captura a la vista.
- Si la lista no basta para entender la pantalla, llama look: la captura llega en el siguiente turno.
  Para leer contenido (correo, página, documento) usa read_text.
- Navegador: usa browser (go, new_tab, search, back, find, tab…) antes que clics.
- Programas: open o window focus. Archivos: find_files y luego open.
- «Toma un print» = screenshot (queda en el portapapeles; se pega con ctrl+v).
- La transcripción trae errores: interpreta la intención. Ruido, el bebé, frases a medias o una charla
  con otra persona: ignore.
- Si falta un dato imprescindible: done con una pregunta corta.
- Enviar, publicar, comprar, pagar o borrar solo si lo pidió; si no, déjalo listo y pregunta con done.
- Palabras nuevas a mitad de la tarea mandan: corrigen, añaden o piden parar.
- Al terminar: done con una frase muy corta en su idioma.
- Las herramientas mcp_* vienen de servidores MCP del usuario; úsalas cuando encajen mejor que la pantalla.
""";

    public static readonly ToolGroup[] Groups =
    [
        new("mouse", "Ratón", "Clic, doble clic, arrastrar y scroll", ["click", "drag", "scroll"]),
        new("keyboard", "Teclado", "Escribir, atajos y teclas", ["type_text", "press_keys"]),
        new("browser", "Navegador", "Ir a sitios, pestañas, buscar, atrás", ["browser"]),
        new("windows", "Programas y ventanas", "Abrir, cambiar, minimizar, cerrar", ["open", "window"]),
        new("read", "Lectura", "Leer el texto de la pantalla y el portapapeles", ["read_text", "clipboard"]),
        new("files", "Archivos", "Buscar archivos del usuario", ["find_files"]),
        new("print", "Print", "Captura al portapapeles", ["screenshot"]),
    ];

    const string Defs = """
[
 {"type":"function","name":"click","description":"Clic en un elemento (mark) o en x,y de la captura.",
  "parameters":{"type":"object","properties":{"mark":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"},
   "button":{"type":"string","enum":["left","right","middle"]},"double":{"type":"boolean"}}}},
 {"type":"function","name":"drag","description":"Arrastra de un punto a otro (mark o x,y).",
  "parameters":{"type":"object","properties":{"from_mark":{"type":"integer"},"from_x":{"type":"integer"},"from_y":{"type":"integer"},
   "to_mark":{"type":"integer"},"to_x":{"type":"integer"},"to_y":{"type":"integer"}}}},
 {"type":"function","name":"scroll","description":"Rueda del ratón sobre un elemento o el centro; to=top/bottom salta al principio/final.",
  "parameters":{"type":"object","properties":{"direction":{"type":"string","enum":["up","down","left","right"]},
   "amount":{"type":"integer","description":"Muescas, por defecto 5."},"to":{"type":"string","enum":["top","bottom"]},
   "mark":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"}}}},
 {"type":"function","name":"type_text","description":"Escribe texto. mark: clic antes en ese campo. replace: borra lo que había. submit: Enter al final.",
  "parameters":{"type":"object","properties":{"text":{"type":"string"},"mark":{"type":"integer"},
   "replace":{"type":"boolean"},"submit":{"type":"boolean"}},"required":["text"]}},
 {"type":"function","name":"press_keys","description":"Combinación de teclas: \"ctrl+c\", \"enter\", \"alt+tab\", \"win\", \"ctrl+shift+t\", \"pagedown\", \"f5\"…",
  "parameters":{"type":"object","properties":{"keys":{"type":"string"},"repeat":{"type":"integer"}},"required":["keys"]}},
 {"type":"function","name":"browser","description":"Navegador activo (Chrome, Edge, Firefox). go: URL en esta pestaña. new_tab: pestaña nueva (con url opcional). search: busca text en el buscador del navegador. find: busca text en la página. tab: ir a la pestaña n (1-9). Más: back, forward, reload, close_tab, next_tab, prev_tab.",
  "parameters":{"type":"object","properties":{"action":{"type":"string","enum":["go","new_tab","search","find","back","forward","reload","close_tab","next_tab","prev_tab","tab"]},
   "url":{"type":"string"},"text":{"type":"string"},"n":{"type":"integer"}},"required":["action"]}},
 {"type":"function","name":"open","description":"Abre una URL, un archivo/carpeta o un programa por nombre (chrome, notepad, explorer, ms-settings:…).",
  "parameters":{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}},
 {"type":"function","name":"window","description":"Ventanas abiertas. list: lista títulos. focus: trae al frente la que contenga name (título o programa). minimize/maximize/close: la de name o la activa.",
  "parameters":{"type":"object","properties":{"action":{"type":"string","enum":["list","focus","minimize","maximize","close"]},"name":{"type":"string"}},"required":["action"]}},
 {"type":"function","name":"read_text","description":"Devuelve texto de la pantalla. window: contenido de la ventana activa (página, correo, documento). focused: el campo con foco. selection: lo seleccionado.",
  "parameters":{"type":"object","properties":{"what":{"type":"string","enum":["window","focused","selection"]}},"required":["what"]}},
 {"type":"function","name":"clipboard","description":"Portapapeles de texto: get lo lee, set pone text.",
  "parameters":{"type":"object","properties":{"action":{"type":"string","enum":["get","set"]},"text":{"type":"string"}},"required":["action"]}},
 {"type":"function","name":"find_files","description":"Busca archivos y carpetas del usuario (Escritorio, Documentos, Descargas, Imágenes, Vídeos, Música, OneDrive) por palabras del nombre. Más recientes primero.",
  "parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}},
 {"type":"function","name":"screenshot","description":"Print al portapapeles (y guardado como PNG). Se pega con ctrl+v.",
  "parameters":{"type":"object","properties":{"area":{"type":"string","enum":["screen","window"]}}}},
 {"type":"function","name":"look","description":"Pide la captura de pantalla para el siguiente turno.",
  "parameters":{"type":"object","properties":{}}},
 {"type":"function","name":"wait","description":"Espera a que algo cargue.",
  "parameters":{"type":"object","properties":{"seconds":{"type":"number"}},"required":["seconds"]}},
 {"type":"function","name":"done","description":"Termina: pedido cumplido, pregunta al usuario o pide confirmación.",
  "parameters":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}},
 {"type":"function","name":"ignore","description":"Lo transcrito no es una orden para ti.",
  "parameters":{"type":"object","properties":{}}}
]
""";

    /// <summary>Herramientas activas: todas menos los grupos apagados (look, wait, done e ignore van siempre).</summary>
    public static JsonArray Build(IEnumerable<string>? disabledGroups)
    {
        var off = new HashSet<string>(
            Groups.Where(g => disabledGroups?.Contains(g.Id) == true).SelectMany(g => g.Tools));
        var all = JsonNode.Parse(Defs)!.AsArray();
        var result = new JsonArray();
        foreach (var t in all.ToList())
        {
            if (off.Contains(t!["name"]!.GetValue<string>())) continue;
            all.Remove(t);
            result.Add(t);
        }
        return result;
    }
}
