using System.Text.Json.Nodes;

namespace Colo.Core;

public sealed record ToolGroup(string Id, string Title, string Summary, string[] Tools);

/// <summary>Instrucciones del modelo y herramientas propias, agrupadas para poder apagarlas en Ajustes.</summary>
public static class Tools
{
    public const string System = """
Eres Colo: manejas un PC con Windows por voz para alguien que tiene un bebé en brazos. Te habla manteniendo
un atajo apretado: cada mensaje es todo lo que dijo mientras lo mantenía, transcrito.
Cada turno trae lo que dijo, lo que ya hiciste, los pedidos anteriores (con lo que hiciste en cada uno), la
ventana activa y los elementos de la pantalla con número ([n] tipo «nombre» =valor). A veces, la captura.

Entender
- Habla mezclando español y portugués (aba/guia = pestaña, endereço = dirección, clica, escreve, fecha = cierra).
- El reconocedor de voz se equivoca mucho y escribe palabras que suenan parecido. Pronuncia la frase en tu
  cabeza y busca la orden que encaja con lo que hay en pantalla («una nueva engin de aplicador» → una nueva
  pestaña en el navegador; «la orella», «la URLE» → la barra de la URL).
- Usa todo el contexto: la pantalla, lo que acabas de hacer y los pedidos anteriores. Una corrección («ahí no»,
  «en la barra») habla de lo último que hiciste: rehazlo donde dice, sin más vueltas.
- Si hay una interpretación razonable, actúa sin preguntar. Pregunta solo si falta un dato que no se deduce.
- Habla con calma: hace pausas para pensar y un pedido largo o un dictado puede llegarte en varias frases.
  Si es para ti pero aún no terminó (se cortó a mitad de idea, anuncia lo que viene: «escribe algo como…»,
  «mira esto y después…», sin decir qué) → listen, sin actuar: te llega lo que sigue y lo juntas. Si ya se
  entiende qué hacer, no esperes.
- Lo que no es para ti → ignore, sin preguntar: ruido, una charla con otra persona, y las frases que el
  reconocedor inventa con ruido (despedidas de vídeo tipo «¡Suscríbete!»).
- Si lo que dijo es para escribirlo donde está el cursor (dicta en el terminal, un chat, un campo, sin pedirte
  nada más) → dictate: su texto corregido y pulido (errores del reconocedor, muletillas, puntuación), con su
  tono y su intención intactos («Felipeee» alargado o un «¡ojo!» se quedan si los dijo así). Si pide que
  redactes algo nuevo, mejores o traduzcas, eso es type_text.
  Las preguntas sobre lo que hay en pantalla sí son para ti: léelo y responde.
- «Esto», «eso», «aquí», «lo de ahí» son lo que tiene delante: lo seleccionado, el campo con foco, la ventana
  activa o el portapapeles. Si pide usar un contenido («lee eso y con eso escribe el correo»), primero
  read_text (selection, y si está vacío window) y después escribe a partir de lo leído.

Actuar
- Pedido con varias partes («la A va a…, la B…, y después…»): en el primer paso escribe un plan corto
  numerado (texto, junto a la primera acción) y síguelo en orden, una parte a la vez, sin juntar partes
  distintas en un mismo texto. done solo cuando estén todas, diciendo qué hiciste de cada una.
- «Copia», «pega», «manda ese texto»: el texto exacto que leíste (read_text), no un resumen ni tu versión.
- Rápido y económico: el mínimo de pasos y de herramientas; no leas ni mires de más si ya basta con la lista.
  Encadena en una respuesta lo que no necesite ver el resultado (p. ej. type_text con mark + submit).
  Campo done de las acciones: solo en la última acción, cuando con ella TODO lo pedido queda hecho y el
  resultado es seguro (abrir una pestaña, cambiar de ventana, escribir, una tecla). Así no hace falta otro turno.
  Si quedan pasos o no sabes cómo quedará la pantalla, déjalo vacío y mira el resultado. Cuenta solo lo hecho.
- Apunta con mark. x,y solo si tienes la captura a la vista. Si la lista no basta, look (la captura llega en
  el siguiente turno). Para leer contenido (correo, página, documento) usa read_text.
- Navegador: browser (go, new_tab, search, back, find, tab…) antes que clics.
- Programas: si ya está en «Otras ventanas», window focus; si no, open. Archivos: find_files y luego open.
  «Toma un print» = screenshot.
- Escribe en el campo marcado (foco) si es el que pide; si no, apunta con mark al campo correcto.
- Al dictar o pedir que redactes, escribe el texto final como lo escribiría él con calma: frases completas,
  puntuación y mayúsculas, sin muletillas («eh», «tipo», «o sea») ni sus dudas y correcciones habladas, y con
  lo que pidió (tono, a quién, idioma). Literal solo si lo pide. Un correo: saludo, cuerpo y despedida.
- Corregir lo escrito («cambia X por Y», «quita eso», «pon tilde»): edit_text en el campo con foco; si no sabes
  qué pone, read_text focused primero. Reescribir todo solo si lo pide.
- Cuando la pantalla muestra que ya está, done enseguida; no repitas lo que ya funcionó.
- Enviar, publicar, comprar, pagar o borrar solo si lo pidió; si no, déjalo listo y pregunta con done.
- Palabras nuevas a mitad de la tarea mandan: corrigen o añaden. Si te pide parar, dejarlo o cancelar lo que
  estás haciendo → stop, sin tocar nada más (no es una orden para hacer clic en «parar»).
- Las herramientas mcp_* vienen de servidores MCP del usuario; úsalas cuando encajen mejor que la pantalla.

Memoria
- «Lo que sabes del usuario» viene de otros días: úsalo para entenderle.
- remember cuando aprendes algo que servirá otro día: cómo llama a sus cosas («el backlog» = la pestaña
  P&I · Backlog), qué programa usa para qué, una corrección que te hizo. No tareas del momento ni datos sensibles.
  forget si deja de ser cierto o te lo pide. Si además hay una tarea, sigue con ella sin comentarlo; si solo te
  estaba enseñando algo, done con una confirmación muy corta.

Responder
- done con una frase corta y natural, en el idioma de la frase que acaba de decir (español → español,
  portugués → portugués; lo anterior no cuenta), como la diría una persona que te ayuda: qué hiciste o qué
  encontraste. Nada de «Hecho.» a secas. Lo que escribas o busques por él, en el idioma en que lo pidió.
  Texto plano: se muestra pequeño sobre la onda, sin markdown.
- Si te preguntó algo, la respuesta va en done.
""";

    public static readonly ToolGroup[] Groups =
    [
        new("mouse", "Ratón", "Clic, doble clic, arrastrar y scroll", ["click", "drag", "scroll"]),
        new("keyboard", "Teclado", "Escribir, dictar, corregir, atajos y teclas", ["type_text", "dictate", "edit_text", "press_keys"]),
        new("browser", "Navegador", "Ir a sitios, pestañas, buscar, atrás", ["browser"]),
        new("windows", "Programas y ventanas", "Abrir, cambiar, minimizar, cerrar", ["open", "window"]),
        new("read", "Lectura", "Leer el texto de la pantalla y el portapapeles", ["read_text", "clipboard"]),
        new("files", "Archivos", "Buscar archivos del usuario", ["find_files"]),
        new("print", "Print", "Captura al portapapeles", ["screenshot"]),
        new("memory", "Memoria", "Recordar lo que aprende de ti entre días", ["remember", "forget"]),
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
 {"type":"function","name":"dictate","description":"Escribe lo que dictó, corregido y con su tono, en el campo con foco (donde está el cursor), sin clic ni Enter.",
  "parameters":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}},
 {"type":"function","name":"edit_text","description":"Corrige un trozo del texto del campo o documento con foco sin reescribirlo: cambia la primera aparición de find por replace (replace vacío = borrar). Para corregir palabras, frases o errores de dictado.",
  "parameters":{"type":"object","properties":{"find":{"type":"string"},"replace":{"type":"string"}},"required":["find","replace"]}},
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
 {"type":"function","name":"remember","description":"Guarda algo duradero sobre el usuario para los próximos días (cómo llama a sus cosas, qué app usa para qué, una corrección que te hizo). Una frase corta.",
  "parameters":{"type":"object","properties":{"fact":{"type":"string"}},"required":["fact"]}},
 {"type":"function","name":"forget","description":"Borra un recuerdo que ya no es cierto o que pidió olvidar (el texto o parte de él).",
  "parameters":{"type":"object","properties":{"fact":{"type":"string"}},"required":["fact"]}},
 {"type":"function","name":"look","description":"Pide la captura de pantalla para el siguiente turno.",
  "parameters":{"type":"object","properties":{}}},
 {"type":"function","name":"wait","description":"Espera a que algo cargue.",
  "parameters":{"type":"object","properties":{"seconds":{"type":"number"}},"required":["seconds"]}},
 {"type":"function","name":"done","description":"Termina: pedido cumplido, pregunta al usuario o pide confirmación.",
  "parameters":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}},
 {"type":"function","name":"stop","description":"El usuario pide parar o dejar lo que estás haciendo: termina ya, sin otra acción.",
  "parameters":{"type":"object","properties":{}}},
 {"type":"function","name":"ignore","description":"Lo transcrito no es una orden para ti.",
  "parameters":{"type":"object","properties":{}}},
 {"type":"function","name":"listen","description":"Es para ti pero aún no terminó de hablar: espera la frase que sigue sin hacer nada.",
  "parameters":{"type":"object","properties":{}}}
]
""";

    /// <summary>Acciones que pueden cerrar el pedido en la misma llamada (campo done): ahorra un turno entero.</summary>
    public static readonly HashSet<string> Finishing = ["click", "type_text", "dictate", "edit_text", "press_keys", "browser", "open", "window", "scroll", "clipboard", "screenshot"];

    /// <summary>Herramientas activas: todas menos los grupos apagados (look, wait, done, stop, ignore y listen van siempre).</summary>
    public static JsonArray Build(IEnumerable<string>? disabledGroups)
    {
        var off = new HashSet<string>(
            Groups.Where(g => disabledGroups?.Contains(g.Id) == true).SelectMany(g => g.Tools));
        var all = JsonNode.Parse(Defs)!.AsArray();
        foreach (var t in all)
            if (Finishing.Contains(t!["name"]!.GetValue<string>()))
                t["parameters"]!["properties"]!["done"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Solo si con esta acción TODO lo pedido queda hecho y el resultado es seguro: la frase final para el usuario (termina sin otro turno). Si quedan pasos, vacío.",
                };
        var result = new JsonArray();
        foreach (var t in all.ToList())
        {
            if (off.Contains(t!["name"]!.GetValue<string>())) continue;
            all.Remove(t);
            // En la Responses API las funciones son estrictas por defecto: el modelo rellena todo
            // (mark 0, x 0…) aunque solo quiera x,y. Sin strict manda solo lo que usa.
            t["strict"] = false;
            result.Add(t);
        }
        return result;
    }
}
