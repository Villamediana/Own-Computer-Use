---
name: graficos
description: cifras y series (cotización, evolución, comparación, partes de un total) con chart.js o d3
---
# Gráficos

- Una cifra protagonista arriba (`.big`) con su variación (verde/rojo con signo y %), luego el gráfico, luego la
  fuente. El gráfico responde a una pregunta; el título dice la conclusión («El dólar subió 3 % en octubre»).
- Elegir: tiempo → línea; comparar categorías → barras horizontales ordenadas; partes de un total → barras
  apiladas o donut solo con ≤5 partes; distribución → histograma.
- chart.js (rápido para lo común):
```js
import Chart from 'chart.js/auto';
const ink = getComputedStyle(document.documentElement).getPropertyValue('--muted').trim();
Chart.defaults.color = ink; Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
new Chart(document.getElementById('g'), { type: 'line',
  data: { labels, datasets: [{ data, borderColor: colo.color(0), backgroundColor: colo.color(0) + '22', fill: true, tension: .3, pointRadius: 0 }] },
  options: { plugins: { legend: { display: false } }, interaction: { mode: 'index', intersect: false },
    scales: { x: { grid: { display: false } }, y: { grid: { color: getComputedStyle(document.documentElement).getPropertyValue('--line') } } } } });
```
  Pon el canvas dentro de un contenedor con altura fija (`<div style="height:260px"><canvas id="g"></canvas></div>`).
- d3 cuando haga falta algo a medida (anotaciones, ejes especiales, transiciones entre estados).
- Series reales: búscalas (web read de una página con la tabla o una API pública conocida: AwesomeAPI
  economia.awesomeapi.com.br para moedas, Frankfurter para cambio histórico, IBGE, BCB SGS). Llama a la API
  desde el JS solo si permite CORS; si no, trae los números con web read y escríbelos en el código.
- Tabla debajo si los valores exactos importan (pocas filas).
