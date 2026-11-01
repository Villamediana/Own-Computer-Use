---
name: palco
description: cómo y cuándo mostrar algo en el palco (léela la primera vez en cada pedido visual)
---
# El palco

Una ventana al lado de la pantalla donde creas, en el momento, lo que ayuda a entender la respuesta. No hay
plantillas: escribes HTML, CSS y JS para este caso. Idea de OpenIntelligentUI (MIT).

## Cuándo
- Sí: cifras con historia (cotización, evolución), comparar opciones, un mapa o un viaje, cómo funciona algo
  (máquina, proceso, concepto), una línea de tiempo, vídeos o imágenes de algo, una calculadora o simulación.
- No: una respuesta de una frase («son 5,43») sin nada más que ver; ahí basta done. Si lo pidió («muéstrame»,
  «explícame con un dibujo»), siempre.
- La respuesta hablada (done) va igual, corta: el palco la acompaña, no la sustituye.

## Datos
- Datos reales vienen de herramientas (web search/read, la pantalla, archivos). Nunca inventes precios, fechas,
  estadísticas ni enlaces. Si un valor es ilustrativo, dilo en la propia página («ejemplo»).
- Pon la fuente y la hora al pie: `<p class="source">Fuente: … · 01/11 14:30</p>`.

## Contrato de stage show
- title: corto. css: solo lo propio (la base ya trae tema, .card, .grid, .big, botones, tablas, .source).
- html: el contenido legible desde el primer momento (títulos, cifras, texto). Nada de `<style>` largos aquí.
- js: módulo ES (puede usar `import` y `await`). Librerías por nombre: `three`, `three/addons/…`, `gsap`, `d3`,
  `chart.js/auto`, `maplibre-gl`, `leaflet`. Otras: `https://cdn.jsdelivr.net/npm/<paquete>@<versión>/+esm`.
- Variables de tema: --bg --surface --ink --ink-2 --muted --line --accent, paleta --c1…--c6 (`colo.color(i)` en
  JS, para canvas y gráficos). Claro y oscuro salen solos si usas las variables.
- Puente: `colo.say(texto)` lo lee en voz alta (narrar pasos); `colo.open(url)` abre en el navegador;
  `colo.ask(texto)` propone una pregunta de vuelta (el usuario la confirma con un clic). Solo desde botones, nunca
  al cargar ni en un temporizador.
- Sin localStorage, cookies ni fetch a servidores inventados. Imágenes y vídeos por URL real (web images/videos).
- El resultado te dice si hubo errores de JS o quedó en blanco: corrígelo y vuelve a llamar show (la página
  entera otra vez).

## Calidad
- Diseño limpio: mucho aire, una cifra o idea principal grande, color solo para lo que importa. Nada de
  gradientes de colores, sombras pesadas ni emojis decorativos.
- Texto en el idioma en que habló. Números con su formato (1.234,56 en pt/es), unidades siempre.
- Cabe en la ventana (unos 640×780) sin scroll horizontal; usa .grid para que se adapte.
- Animación con propósito (que algo aparezca o se mueva para explicar), suave, con botón de pausa si es larga.
