# Colo

Controla el PC con Windows hablando, sin manos. Una onda blanca flotante, siempre encima,
escucha todo el tiempo; cada frase se ejecuta al terminar de decirla. Un cursor azul muestra
dónde va a hacer clic y qué está haciendo.

- **Voz**: Silero VAD + una de tres transcripciones: Parakeet TDT 0.6B v3 en el propio PC (sherpa-onnx, sin tokens,
  ~670 MB la primera vez a `%LOCALAPPDATA%\Colo\models`), el modelo de transcripción de OpenAI/Foundry,
  o ElevenLabs Scribe (`scribe_v2`).
- **Ajustes**: página web (`src/Colo/settings.html`) dentro de WebView2; prueba del micrófono en vivo en Voz.
  Probar la página: `node tests/ui/settings.test.js <carpeta>` (playwright-core).
- **Registro**: `%LOCALAPPDATA%\Colo\colo.log` (micrófono, frases, órdenes, errores).
- **Cerebro**: Azure AI Foundry (`/openai/v1/responses`, nombre del deployment) u OpenAI.
  Cada paso manda una captura con los elementos de UI Automation numerados.
- **Herramientas**: ratón (click, drag, scroll), teclado (type_text con clic/reemplazo/Enter, press_keys),
  navegador (go, new_tab, search, find, back, tab…), ventanas (open, window focus/list/minimize/close),
  lectura (read_text, clipboard), find_files, screenshot, look, wait, done, ignore. Se apagan por grupo en Ajustes.
- **MCP**: servidores por comando (stdio) o URL (Streamable HTTP); se elige qué tools usa. Llegan como `mcp_<servidor>_<tool>`.
- **Rápido y barato**: streaming (cada acción se ejecuta en cuanto el modelo la emite), la pantalla va como lista
  de elementos en texto y la captura solo cuando el modelo la pide (`look`), resultados largos solo un turno.

## Descargar

`Colo.exe` en Releases: un solo archivo, trae .NET dentro, no instala nada ni pide admin.

Uso: clic en la onda = pausar/seguir · arrastrar = mover · clic derecho = menú y Ajustes.
Atajos: `Ctrl+Alt+Espacio` pausar/escuchar, `Ctrl+Alt+X` detener la tarea.
Ajustes en `%APPDATA%\Colo\settings.json` (la API key va cifrada con DPAPI).

## Compilar (desde Linux o Windows)

```bash
dotnet test tests/Colo.Tests
dotnet publish src/Colo -c Release -o dist      # dist/Colo.exe, un solo archivo, sin instalar nada
```

`tools/Preview` renderiza la onda y el cursor a PNG (`Preview shots <dir>`) y corre el bucle real
contra un endpoint (`Preview run <endpoint> "<orden>"`); funciona también en Wine.
