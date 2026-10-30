// Prueba la página de Ajustes en Chromium: npm i playwright-core && node tests/ui/settings.test.js <carpeta-para-print>
// (CHROME=/ruta/a/chrome si no está en la caché de Playwright)
const { chromium } = require('playwright-core');
const assert = (c, m) => { if (!c) { console.log('FAIL:', m); process.exitCode = 1; } else console.log('ok  ', m); };
(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME || process.env.HOME + '/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome', args: ['--no-sandbox'] });
  const page = await browser.newPage({ viewport: { width: 980, height: 700 } });
  // Simula el puente de WebView2.
  await page.addInitScript(() => {
    window.__sent = [];
    let recv = null;
    window.chrome = { webview: { postMessage: m => window.__sent.push(JSON.parse(JSON.stringify(m))), addEventListener: (t, f) => recv = f } };
    window.__deliver = d => recv({ data: d });
  });
  page.on('pageerror', e => { console.log('PAGE ERROR', e.message); process.exitCode = 1; });
  await page.goto('file://' + require('path').resolve(__dirname, '../../src/Colo/settings.html') + '#still');
  const sent = () => page.evaluate(() => window.__sent);
  const last = async t => (await sent()).filter(m => m.type === t).pop();
  assert((await sent())[0]?.type === 'ready', 'pide init al cargar');
  await page.evaluate(() => window.__deliver({
    type: 'init', version: '1.2.0', printsDir: 'C:\\x', memory: ['Su navegador es Comet', '«El backlog» es la pestaña P&I'], mics: [{ id: 'id-a', name: 'Mic A' }, { id: 'id-b', name: 'Mic B' }],
    groups: [{ id: 'mouse', title: 'Ratón', summary: 's' }, { id: 'browser', title: 'Navegador', summary: 's' }],
    settings: { Provider: 'Azure', Endpoint: 'https://a.openai.azure.com', ApiKey: 'k1', Model: '', ReasoningEffort: 'low', SpeechEngine: 'Local',
      TranscribeModel: '', MicId: '', LocalModel: 'base', Language: 'es', Sensitivity: 6, SpeakReplies: false, StartWithWindows: true, Vision: 'Auto', DisabledTools: [], McpServers: [], WidgetX: 5 },
  }));

  // proveedor + key + conectar + elegir modelo
  await page.click('[data-bind=Provider] button[data-v=OpenAI]');
  await page.fill('#apikey', 'sk-nueva');
  await page.click('#connect');
  const lm = await last('listModels');
  assert(lm && lm.provider === 'OpenAI' && lm.apiKey === 'sk-nueva', 'conectar manda proveedor y key');
  await page.evaluate(() => window.__deliver({ type: 'models', ok: true, models: [{ id: 'gpt-5.6-luna', base: 'gpt-5.6' }, { id: 'gpt-4.1', base: 'gpt-4.1' }] }));
  assert(await page.isVisible('#modelCombo .pop .opt[data-id="gpt-4.1"]'), 'la lista de modelos se abre');
  await page.click('#modelCombo .opt[data-id="gpt-4.1"]');
  assert(await page.inputValue('#model') === 'gpt-4.1', 'elegir modelo');
  await page.click('[data-bind=ReasoningEffort] button[data-v=none]');

  // voz
  await page.click('nav button[data-page=voice]');
  assert(await page.isVisible('#p-voice'), 'navega a Voz');
  assert((await page.textContent('#talkKeys')).replace(/\s/g, '') === 'CtrlWin', 'muestra el atajo para hablar');
  assert(!(await page.isChecked('#dictateOn')) && !(await page.isVisible('#dictateKeys')), 'dictar empieza apagado');
  assert(await page.isVisible('#localVoice') && !(await page.isVisible('#voiceModel')), 'solo se ven las opciones de la transcripción elegida');
  assert(!(await page.isVisible('#liveModel')) && !(await page.isVisible('#voiceConn')), 'lo de GPT-Live no se ve si no se elige');
  // dos letras cualesquiera, apretadas a la vez (encender dictar ya pide el atajo)
  await page.click('label.switch:has(#dictateOn)');
  await page.keyboard.down('KeyJ');
  await page.keyboard.down('KeyK');
  await page.keyboard.up('KeyK');
  await page.keyboard.up('KeyJ');
  assert((await page.textContent('#dictateKeys')).replace(/\s/g, '') === 'JK', 'graba un atajo de dos letras');
  await page.click('#recordTalk');
  await page.keyboard.down('KeyJ');
  await page.keyboard.up('KeyJ');
  assert((await page.textContent('#talkKeyHint')).includes('dos teclas'), 'una tecla sola no vale');
  await page.keyboard.down('Control');
  await page.keyboard.down('Shift');
  await page.keyboard.down('KeyK');
  await page.keyboard.up('KeyK');
  assert((await page.textContent('#talkKeys')).replace(/\s/g, '') === 'CtrlMayúsK', 'graba Ctrl+Mayús+K');
  await page.keyboard.up('Shift');
  await page.keyboard.up('Control');
  assert((await sent()).some(m => m.type === 'recordingKeys' && m.on) && (await last('recordingKeys'))?.on === false, 'pausa los atajos mientras graba');
  await page.click('#modeChoice .choice[data-v=live]');
  assert(await page.isVisible('#liveModel') && await page.isVisible('#voiceConn'), 'GPT-Live muestra modelo y conexión');
  assert(!(await page.isVisible('#replySection')), 'en vivo no hay voz de respuesta aparte');
  assert((await page.textContent('#engineHint')).includes('dictar'), 'con dictar encendido, la transcripción queda para el dictado');
  await page.click('[data-bind=VoiceOwn] button[data-v=own]');
  await page.fill('#voiceKey', 'k-voz');
  await page.evaluate(() => document.querySelector('#talkKeys').scrollIntoView());
  await page.waitForTimeout(800);
  await page.screenshot({ path: process.argv[2] + '/voz-atajos.png' });
  await page.evaluate(() => document.querySelector('#voiceConn').scrollIntoView());
  await page.waitForTimeout(800);
  await page.screenshot({ path: process.argv[2] + '/voz-conexion.png' });
  await page.click('#modeChoice .choice[data-v=normal]');
  assert(await page.isVisible('#replySection') && !(await page.isVisible('#liveModel')), 'volver a «le pides y lo hace»');
  await page.click('label.switch:has(#speak)');
  await page.selectOption('#mic', 'id-b');
  await page.click('[data-bind=LocalModel] button[data-v=small]');
  await page.selectOption('#language', 'pt');
  await page.click('#engineChoice .choice[data-v=ElevenLabs]');
  assert(await page.isVisible('#elevenKey') && !(await page.isVisible('#voiceModel')), 'ElevenLabs muestra su llave');
  await page.fill('#elevenKey', 'xi-abc');
  await page.click('#engineChoice .choice[data-v=Api]');
  assert(await page.isVisible('#voiceModel'), 'modelo de transcripción aparece con API');
  assert((await page.textContent('#voiceProblem')).includes('transcripción'), 'dice qué falta: el modelo de transcripción');
  await page.click('[data-bind=VoiceOwn] button[data-v=same]');
  assert((await page.textContent('#voiceSame')).includes('…ueva'), 'muestra qué llave toma de Modelo');
  await page.focus('#voiceModel');
  let vm = await last('listModels');
  assert(vm?.for === 'voice' && vm.apiKey === 'sk-nueva', 'la misma de Modelo: la lista de transcripción usa esa llave');
  await page.evaluate(() => window.__deliver({ type: 'models', for: 'voice', ok: true, models: [{ id: 'gpt-4o-mini-transcribe', base: 'gpt-4o-mini-transcribe' }] }));
  assert(await page.isVisible('#voiceCombo .opt[data-id="gpt-4o-mini-transcribe"]'), 'aparecen los modelos de esa conexión');
  await page.click('[data-bind=VoiceOwn] button[data-v=own]');
  await page.fill('#voiceModel', 'whisper');
  assert(await page.isHidden('#voiceProblem'), 'con el modelo puesto ya no falta nada');
  // respuestas habladas con ElevenLabs: la llave sigue visible con transcripción por API
  assert(await page.isVisible('#elevenReply') && await page.isVisible('#elevenKey'), 'responder hablando muestra voz y llave de ElevenLabs');
  await page.waitForTimeout(700);
  const lv = await last('listVoices');
  assert(lv?.key === 'xi-abc', 'pide las voces con la llave');
  await page.evaluate(() => window.__deliver({ type: 'voices', ok: true, voices: [{ id: 'v1', name: 'Luna', about: 'warm' }, { id: 'v2', name: 'Mateo', about: '' }] }));
  await page.selectOption('#elevenVoiceSel', 'v2');
  await page.click('[data-bind=ElevenTtsModel] button[data-v=eleven_v4_turbo]');
  await page.click('#sayTest');
  assert((await last('sayTest'))?.settings?.ElevenVoiceId === 'v2', 'probar voz manda la voz elegida');
  await page.screenshot({ path: process.argv[2] + '/voz-respuestas.png', fullPage: true });
  await page.evaluate(() => window.__deliver({ type: 'voiceStatus', text: 'Escuchando con «Mic A»' }));
  await page.evaluate(() => window.__deliver({ type: 'voiceHeard', text: 'hola colo', ms: 300 }));
  assert((await page.textContent('#vHeard')).includes('hola colo') && (await page.textContent('#vStatus')).includes('Mic A'), 'prueba del micrófono muestra lo oído');

  // herramientas
  await page.click('nav button[data-page=tools]');
  await page.click('label.switch:has([data-group=browser])');
  await page.click('[data-bind=Vision] button[data-v=Always]');

  // MCP: añadir servidor, conectar, elegir tools, guardar
  await page.click('nav button[data-page=mcp]');
  await page.click('#addServer');
  assert(await page.isVisible('.sheet'), 'abre la hoja MCP');
  await page.fill('#sName', 'Archivos');
  await page.fill('#sTarget', 'npx -y @modelcontextprotocol/server-filesystem C:\\Users\\yo');
  await page.fill('#sExtra', 'A=1');
  await page.click('#sConnect');
  const mt = await last('mcpTools');
  assert(mt && mt.server.Command.startsWith('npx') && mt.server.Env === 'A=1' && mt.server.Kind === 'Command', 'conectar MCP manda el servidor');
  await page.evaluate(() => window.__deliver({ type: 'mcpTools', ok: true, tools: [{ name: 'read_file', description: 'Lee' }, { name: 'write_file', description: 'Escribe' }] }));
  await page.click('label.switch:has(#sAll)');       // apaga "Todas"
  await page.click('.tool:has([data-tool=write_file])');  // desmarca write_file
  await page.click('#sClose');
  assert(!(await page.isVisible('.sheet')), 'cerrar la hoja la guarda');
  assert((await page.textContent('#servers')).includes('Archivos'), 'el servidor aparece en la lista');
  // URL inválida no guarda
  await page.click('#addServer');
  await page.click('#sKind button[data-v=Url]');
  await page.fill('#sTarget', 'no es url');
  await page.click('#sClose');
  assert(await page.isVisible('.sheet') && (await page.textContent('#sStatus')).includes('https'), 'URL inválida avisa y no cierra');
  await page.fill('#sTarget', '');
  await page.click('#sClose');
  assert(!(await page.isVisible('.sheet')) && (await page.$$('.server')).length === 1, 'vacía se cierra sin añadir nada');

  // costo: lo usado por modelo × precio; un precio propio cambia el total al momento
  await page.evaluate(() => window.__deliver({ type: 'init', version: '1', printsDir: '', memory: ['Su navegador es Comet'], mics: [{ id: 'id-b', name: 'Mic B' }], groups: [{ id: 'browser', title: 'Navegador', summary: 's' }],
    settings: { ...flushSave(), ...JSON.parse(JSON.stringify(S)) },
    usage: { asOf: 'octubre de 2026', known: { 'gpt-6-luna': { Input: 0.1, Cached: 0.01, Output: 0.5, PerMinute: 0 } },
      today: { 'gpt-6-luna': { Input: 1000000, Cached: 0, Output: 0, Seconds: 0, Calls: 3 } },
      all: { 'gpt-6-luna': { Input: 2000000, Cached: 0, Output: 0, Seconds: 0, Calls: 9 }, 'otro-modelo': { Input: 1000000, Cached: 0, Output: 0, Seconds: 0, Calls: 1 } } } }));
  await page.click('nav button[data-page=cost]');
  assert((await page.textContent('#costAll')) === 'US$ 0,20' && (await page.textContent('#costToday')) === 'US$ 0,10', 'costo total y de hoy');
  assert((await page.textContent('.cost-row[data-model="otro-modelo"]')).includes('sin precio'), 'avisa el modelo sin precio');
  await page.fill('.cost-row[data-model="otro-modelo"] input[data-k=Input]', '1');
  assert((await page.textContent('#costAll')) === 'US$ 1,20', 'un precio propio suma al total');

  // general
  await page.click('nav button[data-page=general]');
  await page.click('label.switch:has(#autostart)');
  assert((await page.textContent('#facts')).includes('Su navegador es Comet'), 'muestra lo que Colo sabe');
  await page.click('[data-forget="0"]');
  assert((await last('forgetFact'))?.text === 'Su navegador es Comet' && !(await page.textContent('#facts')).includes('Comet'), 'olvidar un recuerdo');
  assert(!(await page.isVisible('footer')) && !(await page.$('#save')), 'sin botones de guardar');

  await page.waitForTimeout(600);
  const sv = (await last('save'))?.settings;
  assert(!!(await page.$('#sideFoot.flash')), '«Guardado» aparece un momento');
  await page.waitForTimeout(1500);
  assert(!(await page.$('#sideFoot.flash')), 'y se va solo');
  const checks = {
    Provider: sv?.Provider === 'OpenAI', ApiKey: sv?.ApiKey === 'sk-nueva', Model: sv?.Model === 'gpt-4.1', Effort: sv?.ReasoningEffort === 'none',
    Speak: sv?.SpeakReplies === true, Mic: sv?.MicId === 'id-b', Engine: sv?.SpeechEngine === 'Api', Voice: sv?.TranscribeModel === 'whisper',
    Tools: JSON.stringify(sv?.DisabledTools) === '["browser"]', Vision: sv?.Vision === 'Always', Autostart: sv?.StartWithWindows === false,
    Eleven: sv?.ElevenLabsKey === 'xi-abc',
    Reply: sv?.ReplyVoice === 'ElevenLabs' && sv?.ElevenVoiceId === 'v2' && sv?.ElevenVoiceName === 'Mateo' && sv?.ElevenTtsModel === 'eleven_v4_turbo',
    Whisper: sv?.LocalModel === 'small' && sv?.Language === 'pt',
    Mcp: sv?.McpServers?.length === 1 && JSON.stringify(sv.McpServers[0].Tools) === '["read_file"]' && sv.McpServers[0].Enabled === true,
    Keeps: sv?.WidgetX === 5,
    Prices: sv?.Prices?.['otro-modelo']?.Input === 1,
    Keys: sv?.TalkHotkey === 'Ctrl+Shift+K' && sv?.DictateHotkey === 'J+K',
    Live: sv?.Live === false && sv?.LiveModel === 'gpt-live-1' && sv?.VoiceOwn === true && sv?.VoiceKey === 'k-voz',
  };
  for (const [k, v] of Object.entries(checks)) assert(v, 'se guarda solo con ' + k);

  // Lo tecleado justo antes de cerrar con la X no se pierde: Colo lo recoge con flushSave().
  await page.click('nav button[data-page=model]');
  await page.fill('#endpoint', 'https://otro.openai.azure.com');
  const pending = await page.evaluate(() => flushSave());
  assert(pending?.Endpoint === 'https://otro.openai.azure.com', 'al cerrar recoge lo pendiente');
  assert(await page.evaluate(() => flushSave()) === null, 'y no lo repite');
  const before = (await sent()).filter(m => m.type === 'save').length;
  await page.click('nav button[data-page=tools]');
  await page.click('[data-bind=Vision] button[data-v=Auto]');
  await page.keyboard.press('Escape');
  const after = await sent();
  assert(after.filter(m => m.type === 'save').length === before + 1 && after.pop().type === 'close', 'Esc guarda lo pendiente y cierra');
  await page.screenshot({ path: process.argv[2] + '/after-mcp.png' });
  await browser.close();
})();
