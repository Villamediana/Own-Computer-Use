// Prueba el palco en Chromium: el host (stage.html) con páginas creadas por StageDoc servidas en https://palco.colo.
// node tests/ui/stage.test.js <carpeta-con-docs> <carpeta-para-print>
const { chromium } = require('playwright-core');
const fs = require('fs'), path = require('path');
const assert = (c, m) => { if (!c) { console.log('FAIL:', m); process.exitCode = 1; } else console.log('ok  ', m); };
(async () => {
  const [docs, out] = process.argv.slice(2);
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 680, height: 760 } });
  await page.route('https://colo.stage/**', r => r.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.resolve(__dirname, '../../src/Colo/stage.html')) }));
  await page.route('https://palco.colo/**', r => r.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(docs, path.basename(new URL(r.request().url()).pathname))) }));
  // Simula el puente de WebView2 del host.
  await page.addInitScript(() => {
    if (location.host !== 'colo.stage') return;
    window.__sent = [];
    let recv = null;
    window.chrome = { webview: { postMessage: m => window.__sent.push(JSON.parse(JSON.stringify(m))), addEventListener: (t, f) => recv = f } };
    window.__deliver = d => recv({ data: d });
  });
  page.on('pageerror', e => console.log('HOST ERROR', e.message));
  await page.goto('https://colo.stage/stage.html');
  const sent = () => page.evaluate(() => window.__sent);
  assert((await sent())[0]?.type === 'ready', 'host avisa que está listo');
  async function show(name) {
    await page.evaluate(() => window.__sent = []);
    await page.evaluate(n => window.__deliver({ type: 'show', src: 'https://palco.colo/' + n }), name);
    for (let i = 0; i < 80; i++) {
      const r = (await sent()).find(m => m.type === 'rendered');
      if (r) return { r, all: await sent() };
      await page.waitForTimeout(250);
    }
    return { r: null, all: await sent() };
  }
  let { r } = await show('chart.html');
  assert(r && r.ran && !r.blank && r.errors.length === 0, 'gráfico con chart.js del CDN: sin errores ' + JSON.stringify(r));
  await page.screenshot({ path: out + '/palco-grafico.png' });
  ({ r } = await show('badimport.html'));
  assert(r && !r.ran, 'import que no existe: ran=false ' + JSON.stringify(r));
  ({ r } = await show('throws.html'));
  assert(r && r.errors.some(e => /null|reading/.test(e)), 'error de JS llega al host ' + JSON.stringify(r));
  ({ r } = await show('blank.html'));
  assert(r && r.blank, 'página vacía se detecta ' + JSON.stringify(r));
  ({ r } = await show('three.html'));
  assert(r && r.ran && r.errors.length === 0, 'three.js + OrbitControls ' + JSON.stringify(r));
  await page.waitForTimeout(600);
  await page.screenshot({ path: out + '/palco-3d.png' });
  ({ r } = await show('map.html'));
  assert(r && r.ran && r.errors.length === 0, 'mapa MapLibre + OpenFreeMap ' + JSON.stringify(r));
  await page.waitForTimeout(2500);
  await page.screenshot({ path: out + '/palco-mapa.png' });
  let all;
  ({ r, all } = await show('gsap.html'));
  assert(r && r.ran && r.errors.length === 0, 'gsap ' + JSON.stringify(r));
  assert(all.some(m => m.type === 'say' && /sinais/.test(m.text)), 'colo.say llega a Colo');
  // ask: no sale hasta que se confirma
  await page.frameLocator('#frame').locator('button').click();
  await page.waitForTimeout(200);
  assert(!(await sent()).some(m => m.type === 'ask'), 'ask no sale sin confirmar');
  assert(await page.isVisible('#ask.on'), 'ask pide confirmación');
  await page.screenshot({ path: out + '/palco-ask.png' });
  await page.click('#askGo');
  assert((await sent()).some(m => m.type === 'ask' && m.text === 'explica a retropropagação'), 'ask confirmado llega a Colo');
  // La página no puede tocar el host.
  const touched = await page.frameLocator('#frame').locator('body').evaluate(() => { try { return !!parent.document.body; } catch { return false; } });
  assert(!touched, 'la página no alcanza el DOM del host');
  await browser.close();
})();
