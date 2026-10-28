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
  await page.click('label.switch:has(#speak)');
  await page.selectOption('#mic', 'id-b');
  await page.click('[data-bind=LocalModel] button[data-v=small]');
  await page.selectOption('#language', 'pt');
  await page.click('[data-bind=SpeechEngine] button[data-v=ElevenLabs]');
  assert(await page.isVisible('#elevenKey') && !(await page.isVisible('#voiceModel')), 'ElevenLabs muestra su llave');
  await page.fill('#elevenKey', 'xi-abc');
  await page.click('[data-bind=SpeechEngine] button[data-v=Api]');
  assert(await page.isVisible('#voiceModel'), 'modelo de transcripción aparece con API');
  await page.fill('#voiceModel', 'whisper');
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
  await page.click('#sSave');
  assert(!(await page.isVisible('.sheet')), 'cierra la hoja');
  assert((await page.textContent('#servers')).includes('Archivos'), 'el servidor aparece en la lista');
  // URL inválida no guarda
  await page.click('#addServer');
  await page.click('#sKind button[data-v=Url]');
  await page.fill('#sTarget', 'no es url');
  await page.click('#sSave');
  assert(await page.isVisible('.sheet') && (await page.textContent('#sStatus')).includes('https'), 'URL inválida avisa y no guarda');
  await page.click('#sCancel');

  // general
  await page.click('nav button[data-page=general]');
  await page.click('label.switch:has(#autostart)');
  assert((await page.textContent('#facts')).includes('Su navegador es Comet'), 'muestra lo que Colo sabe');
  await page.click('[data-forget="0"]');
  assert((await last('forgetFact'))?.text === 'Su navegador es Comet' && !(await page.textContent('#facts')).includes('Comet'), 'olvidar un recuerdo');
  assert(await page.isVisible('#dirty.on'), 'marca cambios sin guardar');

  await page.click('#save');
  const sv = (await last('save'))?.settings;
  const checks = {
    Provider: sv?.Provider === 'OpenAI', ApiKey: sv?.ApiKey === 'sk-nueva', Model: sv?.Model === 'gpt-4.1', Effort: sv?.ReasoningEffort === 'none',
    Speak: sv?.SpeakReplies === true, Mic: sv?.MicId === 'id-b', Engine: sv?.SpeechEngine === 'Api', Voice: sv?.TranscribeModel === 'whisper',
    Tools: JSON.stringify(sv?.DisabledTools) === '["browser"]', Vision: sv?.Vision === 'Always', Autostart: sv?.StartWithWindows === false,
    Eleven: sv?.ElevenLabsKey === 'xi-abc',
    Reply: sv?.ReplyVoice === 'ElevenLabs' && sv?.ElevenVoiceId === 'v2' && sv?.ElevenVoiceName === 'Mateo' && sv?.ElevenTtsModel === 'eleven_v4_turbo',
    Whisper: sv?.LocalModel === 'small' && sv?.Language === 'pt',
    Mcp: sv?.McpServers?.length === 1 && JSON.stringify(sv.McpServers[0].Tools) === '["read_file"]' && sv.McpServers[0].Enabled === true,
    Keeps: sv?.WidgetX === 5,
  };
  for (const [k, v] of Object.entries(checks)) assert(v, 'guardar lleva ' + k);
  await page.screenshot({ path: process.argv[2] + '/after-mcp.png' });
  await browser.close();
})();
