# Colo no celular — avaliação e plano

## Contexto
O Colo (~/Colo, C# .NET 8, v1.7.0) funciona no Windows. O Miguel quer o mesmo produto no Android: mesma
velocidade, mesmo comportamento, sem compartilhar dados entre os dois (cada um com suas chaves). Pergunta:
transformar em multiplataforma ou fazer uma versão nova?

## O que o código mostra
- O Colo já está dividido em dois: **Colo.Core** (`net8.0`, sem nada de Windows) e **Colo** (WinForms, Win32, UIA).
- No Core já ficam o cérebro e quase tudo que define "como ele é": `Agent.cs` (loop, done/listen, repetição,
  contexto de 10 min), `AiClient.cs` (Foundry/OpenAI streaming), `Tools.cs` (prompt + tools), `Memory.cs`,
  `TokenMeter.cs`, `Mcp.cs`, `Speech.cs` (detector por volume + Whisper.net), `ElevenLabsStt/Tts.cs`,
  `WakeWord.cs` (ONNX «Luna»), `Spring.cs`/`Motion.cs`, `Settings.cs`.
- A fronteira com o sistema é uma interface só: `IComputer` (`ObserveAsync` + `ExecuteAsync`) em `Computer.cs`.
  `WindowsComputer.cs` é a implementação de Windows.
- As dependências do Core têm versão Android: Whisper.net (runtime android-arm64), ONNX Runtime (AAR no NuGet),
  NAudio.Core (gerenciado puro).
- Servidor já tem JDK 21, Android SDK 36, NDK e keystores — dá pra compilar APK aqui (sem emulador).

## Recomendação: um repositório, um núcleo, duas "cabeças"
Não é nem "um app multiplataforma único" nem "um app novo do zero":

- **Colo** (Windows) — fica exatamente como está.
- **Colo.Android** (novo) — app Android em C# (.NET for Android, **sem MAUI**) que usa o **mesmo Colo.Core**.

Por quê:
- **Igual de verdade**: prompt, tools, loop, memória, voz e wake word são o mesmo código. Corrigiu/ajustou
  no Core → vale nos dois no próximo build. Reescrever em Kotlin seria duplicar ~3 mil linhas e elas iriam
  se afastar com o tempo.
- **Não mexe no que funciona**: MAUI/Avalonia obrigaria a refazer a parte Windows (onda layered, cursor no
  vsync, WebView2) e não dá overlay nem AccessibilityService no Android — teria que ser nativo de qualquer jeito.
- **Mesma velocidade**: o tempo é dominado pela ida ao modelo (mesmo endpoint, mesmo loop). Whisper base no
  celular arm64 fica ~0,3–0,8 s por frase; Scribe continua como opção.
- Instaladores separados: `Colo.exe` e `Colo.apk`, cada um com Ajustes e chaves próprias.

## O que muda no Core (pequeno)
- `Tools.cs`: prompt e lista de tools por plataforma (`Tools.System(Platform)`, `Groups(Platform)`): "PC com
  Windows" ↔ "celular Android"; regras de Entender/Actuar/Responder/Memoria idênticas.
- `Mcp.cs`: no Android só servidores por URL (stdio não existe lá).
- `Settings.cs`: campos de Windows (MicId WASAPI, StartWithWindows, ReplyVoice.Windows) ganham equivalente
  neutro; formato do JSON continua compatível com o atual.

## O que é novo no Android (`src/Colo.Android/`)
- **AccessibilityService** = `AndroidComputer : IComputer`
  - Observar: árvore de nós → `Mark` (mesma lista numerada), `takeScreenshot` só quando o modelo pede `look`.
  - Ações: click → toque (right = toque longo, double), drag → swipe, scroll → ACTION_SCROLL/swipe,
    type_text/edit_text → ACTION_SET_TEXT/seleção, press_keys → back/home/recentes/notificações/enter,
    open → app por nome (PackageManager) ou URL/intent, window → apps recentes/trocar de app,
    browser → Chrome por intent + barra de endereço, read_text, clipboard, find_files (MediaStore),
    screenshot → Pictures, remember/forget iguais.
- **Onda** como overlay de acessibilidade (por cima de tudo, sem permissão extra), mesmo desenho/molas do
  `WavePainter` portado pro Canvas; **cursor azul** como indicador do toque com a mesma animação.
- **Microfone**: AudioRecord 16 kHz mono num foreground service (exigência do Android 14+), alimentando o
  mesmo `Listener`. «Luna» e "Siempre escuchando" iguais.
- **Ajustes**: o mesmo `settings.html` num WebView com ponte JS (reaproveita a página e o teste playwright).
- **Chave**: Android Keystore no lugar do DPAPI.

## Limitações do Android a saber
- Apps com FLAG_SECURE (banco) não deixam capturar; apps de jogo/Flutter às vezes não expõem nós → cai em x,y
  com captura.
- Escuta contínua gasta bateria (lição da Luna): escutar só com a tela ligada.
- Só por APK (sideload); Play Store não aceita esse uso de acessibilidade.
- Não compartilha nada com a Luna (app separado), embora a Luna já controle a tela do celular por outro caminho.

## Passos
1. Instalar .NET 10 SDK + workload android em `~/.dotnet` (o 8.0.427 atual não tem workload Android suportada);
   Colo.Core segue `net8.0` (referenciável por `net10.0-android`), Windows não muda.
2. Core: prompt/tools por plataforma + Mcp só URL no Android; `dotnet test tests/Colo.Tests` + CI windows.yml verde.
3. `Colo.Android`: AccessibilityService/AndroidComputer, overlay da onda e cursor, serviço de microfone, Ajustes.
4. Testes do mapeamento tool → ação Android (lógica pura, no Core/tests).
5. `dotnet publish src/Colo.Android -f net10.0-android -c Release` assinado com keystore de `~/keystores`;
   release `Colo.apk` em Villamediana/Own-Computer-Use junto do `Colo.exe`.

## Verificação
- `dotnet test` no Core; CI Windows continua passando (Colo.exe idêntico).
- APK instalado no celular do Miguel: ativar acessibilidade, «Luna, abre o Chrome e busca X», ditado em
  WhatsApp, correção «ahí no», memória. Sem emulador.
