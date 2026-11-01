---
name: diagramas
description: flujos, procesos, arquitecturas, jerarquías y líneas de tiempo en SVG
---
# Diagramas en SVG

- SVG inline con `viewBox` y `width="100%"`: se escala solo. Texto dentro del SVG con `fill="var(--ink)"`,
  cajas `fill="var(--surface)" stroke="var(--line)"`, lo importante en `var(--accent)`.
- Composición: de izquierda a derecha o de arriba abajo, alineado a una rejilla (múltiplos de 8), cajas del
  mismo tamaño por nivel, flechas con `marker-end`. Máximo ~9 cajas a la vista; si hay más, agrupa.
- Etiquetas cortas en las cajas; la explicación va en texto fuera del diagrama o al pasar el ratón (`<title>`).
- Paso a paso: botones «Anterior/Siguiente» que resaltan una parte (cambia la clase y anima opacidad con CSS
  transitions); al cambiar de paso `colo.say()` puede narrarlo si pidió que se lo expliques.
- Línea de tiempo: eje horizontal, hitos como puntos con fecha arriba y texto abajo, alternando para no pisarse.
- Para mostrar una pantalla del usuario explicada: dibuja un esquema simplificado de la ventana (rectángulos y
  rótulos), nunca una captura inventada, y marca con número y color dónde hacer clic.
