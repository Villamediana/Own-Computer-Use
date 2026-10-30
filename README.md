# Colo

Controla el PC con Windows hablando. Mantienes un atajo (por defecto `Ctrl+Win`), hablas y sueltas: transcribe
y lo hace. Una onda blanca aparece mientras hablas o trabaja y se va sola. Un cursor azul muestra dónde va a
hacer clic y qué está haciendo.

- **Movimiento**: el ratón real y el cursor azul se deslizan juntos al ritmo del monitor (DwmFlush), con curva de
  jerk mínimo y un arco leve (`Motion`). Todo lo que aparece entra con muelle (`Spring`): la flecha rebota al
  aparecer, la etiqueta se estira al texto nuevo, el clic hunde la flecha y suelta ondas y chispas; la onda es una
  gota de vidrio que se aplasta y estira al cambiar de estado, y la frase sube con rebote.
- **Atajos que se mantienen**: dos teclas cualesquiera, también dos letras cercanas (se aprieta una y luego la otra);
  una letra del atajo se retiene al bajar y, si no llega la otra, se reenvía tal cual. Con otra tecla encima
  (Ctrl+Win+D) era otro atajo y no transcribe. Opcional: un atajo de **dictar**: transcribe, una llamada corta al
  modelo (sin herramientas ni pantalla) lo corrige con tu tono y se escribe donde está el cursor. Sin él, el
  modelo decide cuándo es un dictado y usa `dictate`.
- **Parar**: si hablas a mitad de una tarea, el paso en curso se corta y el modelo lee lo nuevo antes de seguir;
  «para», «déjalo» → `stop`, sin tocar nada más.
- **GPT-Live** (opcional): conversación en vivo por WebSocket (`/openai/v1/live/sessions`). Mantener el atajo
  abre el micrófono (y la llamada si no hay); soltarlo lo silencia pero la llamada sigue. Lo que haya que hacer en
  el PC, GPT-Live lo delega en Colo (delegación al cliente) y dice el resultado con su voz; cuelga sola cuando la
  tarea terminó y lleva 8 s callada.

- **Voz**: el micrófono se abre al apretar el atajo y se cierra al soltarlo (nada queda escuchando en segundo plano).
  Lo dicho va a una de tres transcripciones: Whisper en el propio PC (base o small, sin tokens, con idioma fijo),
  el modelo de transcripción de OpenAI/Foundry (p. ej. gpt-4o-mini-transcribe) o ElevenLabs Scribe; estas dos y
  GPT-Live entienden el idioma que hables. Whisper y la API llevan un glosario con los programas abiertos.
  La API y GPT-Live pueden usar otra conexión (endpoint y llave) que la del modelo.
- **Respuestas habladas** (opcional): ElevenLabs v4 con etiquetas de emoción ([laughs], [warmly]…) que la voz
  interpreta y no se ven en pantalla, o la voz de Windows. El micrófono se pausa mientras habla.
- **Contexto**: recuerda los últimos pedidos y lo que hizo en cada uno («ahí no, en la barra»); se olvida solo
  tras 10 minutos sin pedidos.
- **Memoria**: con remember/forget el modelo guarda lo que aprende de ti (cómo llamas a tus cosas, qué app usas)
  y lo usa otros días (`%APPDATA%\Colo\memory.json`; se ve y se borra en Ajustes › General).
- **Cada turno** lleva las demás ventanas abiertas y qué campo tiene el foco y qué pestaña está activa.
- **Caché**: instrucciones y tools van primero e iguales en cada paso, luego memoria y pedidos anteriores, y al
  final la pantalla; con `prompt_cache_key` fijo el proveedor reutiliza el prefijo.
- **Económico**: las acciones traen un campo `done` opcional; si con la última acción el pedido queda hecho,
  termina sin otra llamada al modelo (una orden simple = una llamada). Tope de 25 pasos por orden.
- **Tokens**: el menú de la onda muestra los de hoy y el total (`%APPDATA%\Colo\usage.json`); el registro, los de cada orden.
- **Ajustes**: página web (`src/Colo/settings.html`) dentro de WebView2, sin botón de guardar: cada cambio se guarda
  solo (~0,4 s) y lo pesado (micrófono, MCP, atajos) se aplica al cerrar; prueba del micrófono en vivo en Voz.
  Probar la página: `node tests/ui/settings.test.js <carpeta>` (playwright-core).
- **Registro**: `%LOCALAPPDATA%\Colo\colo.log` (micrófono, frases, órdenes, errores).
- **Cerebro**: Azure AI Foundry (`/openai/v1/responses`, nombre del deployment) u OpenAI.
  Cada paso manda una captura con los elementos de UI Automation numerados.
- **Herramientas**: ratón (click, drag, scroll), teclado (type_text con clic/reemplazo/Enter, edit_text para
  corregir un trozo sin reescribirlo, press_keys),
  navegador (go, new_tab, search, find, back, tab…), ventanas (open, window focus/list/minimize/close),
  lectura (read_text, clipboard), find_files, screenshot, look, wait, done, ignore. Se apagan por grupo en Ajustes.
- **MCP**: servidores por comando (stdio) o URL (Streamable HTTP); se elige qué tools usa. Llegan como `mcp_<servidor>_<tool>`.
- **Rápido y barato**: streaming (cada acción se ejecuta en cuanto el modelo la emite), la pantalla va como lista
  de elementos en texto y la captura solo cuando el modelo la pide (`look`), resultados largos solo un turno.

## Descargar

`Colo.exe` en Releases: un solo archivo, trae .NET dentro, no instala nada ni pide admin.

Uso: clic en la onda = pausar/seguir · arrastrar = mover · clic derecho = menú y Ajustes.
Atajos: mantener `Ctrl+Win` para hablarle (se cambia en Ajustes › Voz), `Ctrl+Alt+Espacio` pausar/seguir,
`Ctrl+Alt+X` detener la tarea.
Ajustes en `%APPDATA%\Colo\settings.json` (las API keys van cifradas con DPAPI).

## Compilar (desde Linux o Windows)

```bash
dotnet test tests/Colo.Tests
dotnet publish src/Colo -c Release -o dist      # dist/Colo.exe, un solo archivo, sin instalar nada
```

`tools/Preview` renderiza la onda y el cursor a PNG (`Preview shots <dir>`) y corre el bucle real
contra un endpoint (`Preview run <endpoint> "<orden>"`); funciona también en Wine.
