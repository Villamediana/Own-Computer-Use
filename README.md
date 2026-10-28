# Colo

Controla el PC con Windows hablando, sin manos. Una onda blanca flotante, siempre encima,
escucha todo el tiempo; cada frase se ejecuta al terminar de decirla. Un cursor azul muestra
dónde va a hacer clic y qué está haciendo.

- **Voz**: Parakeet TDT 0.6B v3 + Silero VAD en el propio PC (sherpa-onnx). No gasta tokens.
  Se descarga sola la primera vez (~670 MB) a `%LOCALAPPDATA%\Colo\models`.
- **Cerebro**: Azure AI Foundry (`/openai/v1/responses`, nombre del deployment) u OpenAI.
  Cada paso manda una captura con los elementos de UI Automation numerados.
- **Herramientas del modelo**: click, type_text, press_keys, scroll, open, find_files, screenshot (al portapapeles), wait, done, ignore.

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
