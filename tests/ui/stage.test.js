// Prueba el palco en Chromium: el host (stage.html) con páginas pasadas por StageDoc.Wrap servidas en https://palco.colo.
// node tests/ui/stage.test.js <carpeta-con-docs> <carpeta-para-print>
const { chromium } = require('playwright-core');
const fs = require('fs'), path = require('path');
const assert = (c, m) => { if (!c) { console.log('FAIL:', m); process.exitCode = 1; } else console.log('ok  ', m); };
(async () => {
  const [docs, out] = process.argv.slice(2);
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'] });
  for (const scheme of ['light', 'dark']) {
    const page = await browser.newPage({ viewport: { width: 740, height: 860 }, colorScheme: scheme });
    await page.route('https://colo.stage/**', r => r.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.resolve(__dirname, '../../src/Colo/stage.html')) }));
    await page.route('https://palco.colo/**', r => r.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(docs, path.basename(new URL(r.request().url()).pathname))) }));
    await page.addInitScript(() => {
      if (location.host !== 'colo.stage') return;
      window.__sent = [];
      let recv = null;
      window.chrome = { webview: { postMessage: m => window.__sent.push(JSON.parse(JSON.stringify(m))), addEventListener: (t, f) => recv = f } };
      window.__deliver = d => recv({ data: d });
    });
    page.on('pageerror', e => { console.log('HOST ERROR', e.message); process.exitCode = 1; });
    await page.goto('https://colo.stage/stage.html');
    const sent = () => page.evaluate(() => window.__sent);
    const deliver = m => page.evaluate(m => window.__deliver(m), m);
    // creando
    await deliver({ type: 'loading', title: 'Como funciona uma rede neural', brief: 'Neurônios em camadas, a informação atravessando e o erro voltando para ajustar os pesos.' });
    await deliver({ type: 'progress', chars: 4200 });
    await page.waitForTimeout(900);
    assert(await page.isVisible('#making:not(.off)') && /4,2 mil/.test(await page.textContent('#mStatus')), `${scheme}: tela de criando com progresso`);
    await page.screenshot({ path: `${out}/palco-criando-${scheme}.png` });
    if (scheme === 'dark') { await browser.close(); break; }
    assert((await sent())[0]?.type === 'ready', 'host avisa que está listo');
    async function show(name) {
      await page.evaluate(() => window.__sent = []);
      await deliver({ type: 'show', src: 'https://palco.colo/' + name, title: name });
      for (let i = 0; i < 80; i++) {
        const r = (await sent()).find(m => m.type === 'rendered');
        if (r) return { r, all: await sent() };
        await page.waitForTimeout(250);
      }
      return { r: null, all: await sent() };
    }
    let { r } = await show('ok.html');
    assert(r && r.errors.length === 0 && !r.blank, 'gsap + roughjs + echarts por nombre: sin errores ' + JSON.stringify(r));
    assert(await page.isVisible('#making.off'), 'lo creado reemplaza a «creando»');
    ({ r } = await show('throws.html'));
    assert(r && r.errors.some(e => /null|reading/.test(e)), 'error de JS llega al host ' + JSON.stringify(r));
    ({ r } = await show('blank.html'));
    assert(r && r.blank, 'página vacía se detecta');
    let all;
    ({ r, all } = await show('say.html'));
    assert(all.some(m => m.type === 'say' && /sinais/.test(m.text)), 'colo.say llega a Colo');
    await page.frameLocator('#frame').locator('button').click();
    await page.waitForTimeout(300);
    assert(!(await sent()).some(m => m.type === 'ask') && await page.isVisible('#ask.on'), 'ask pide confirmación');
    await page.click('#askGo');
    assert((await sent()).some(m => m.type === 'ask'), 'ask confirmado llega a Colo');
    const touched = await page.frameLocator('#frame').locator('body').evaluate(() => { try { return !!parent.document.body; } catch { return false; } });
    assert(!touched, 'la página no alcanza el DOM del host');
    // barra: aparece al acercarse arriba y no tapa lo de abajo
    await page.mouse.move(370, 3);
    await page.waitForTimeout(350);
    assert(await page.isVisible('#bar.show'), 'barra aparece arriba');
    await page.click('#close');
    assert((await sent()).some(m => m.type === 'close'), 'cerrar');
    await deliver({ type: 'failed', text: 'No salió: HTTP 429' });
    await page.waitForTimeout(400);
    assert(await page.isVisible('#retry'), 'falló: botón de reintentar');
    await page.click('#retry');
    assert((await sent()).some(m => m.type === 'retry'), 'reintentar');
    await page.close();
  }
})();
