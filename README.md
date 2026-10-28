# Colo

Controla el PC con Windows hablando, sin manos. Una onda blanca flotante, siempre encima,
escucha todo el tiempo; cada frase se ejecuta al terminar de decirla. Un cursor azul muestra
dónde va a hacer clic y qué está haciendo.

- **Voz**: detector por volumen + una de tres transcripciones: Whisper en el propio PC (base o small, sin tokens,
  con glosario y los programas abiertos como pista), el modelo de transcripción de OpenAI/Foundry, o ElevenLabs Scribe.
- **«Luna»**: dormida no se ve nada (ni la onda ni el icono de la bandeja) y solo un detector local oye su nombre (el de la Luna del celular:
  openWakeWord + clasificador entrenado con «luna» de personas reales; ONNX Runtime, ~3 MB). Al oír «Luna» o
  el atajo (Ctrl+Alt+L, se cambia en Ajustes) aparece y atiende; vuelve a dormir tras 45 s sin hablarle.
  Dormida no transcribe ni manda nada al modelo. Se puede volver a «Siempre escuchando» en Ajustes › Voz.
- **Respuestas habladas** (opcional): ElevenLabs v4 con etiquetas de emoción ([laughs], [warmly]…) que la voz
  interpreta y no se ven en pantalla, o la voz de Windows. El micrófono se pausa mientras habla.
- **Contexto**: recuerda los últimos pedidos y lo que hizo en cada uno («ahí no, en la barra»); se olvida solo
  tras 10 minutos sin pedidos.
- **Memoria**: con remember/forget el modelo guarda lo que aprende de ti (cómo llamas a tus cosas, qué app usas)
  y lo usa otros días (`%APPDATA%\Colo\memory.json`; se ve y se borra en Ajustes › General).
- **Cada turno** lleva las demás ventanas abiertas y qué campo tiene el foco y qué pestaña está activa.
- **Económico**: las acciones traen un campo `done` opcional; si con la última acción el pedido queda hecho,
  termina sin otra llamada al modelo (una orden simple = una llamada). Tope de 25 pasos por orden.
- **Tokens**: el menú de la onda muestra los de hoy y el total (`%APPDATA%\Colo\usage.json`); el registro, los de cada orden.
- **Ajustes**: página web (`src/Colo/settings.html`) dentro de WebView2; prueba del micrófono en vivo en Voz.
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
Atajos: `Ctrl+Alt+L` hablarle sin decir «Luna», `Ctrl+Alt+Espacio` pausar/escuchar, `Ctrl+Alt+X` detener la tarea.
Ajustes en `%APPDATA%\Colo\settings.json` (la API key va cifrada con DPAPI).

## Compilar (desde Linux o Windows)

```bash
dotnet test tests/Colo.Tests
dotnet publish src/Colo -c Release -o dist      # dist/Colo.exe, un solo archivo, sin instalar nada
```

`tools/Preview` renderiza la onda y el cursor a PNG (`Preview shots <dir>`) y corre el bucle real
contra un endpoint (`Preview run <endpoint> "<orden>"`); funciona también en Wine.
