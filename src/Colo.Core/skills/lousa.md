---
name: lousa
description: explicar cómo funciona algo con un dibujo animado paso a paso (lousa), narrado en voz
---
# Lousa animada

Para «explícame cómo funciona…» (una red neuronal, el corazón, un motor, un algoritmo, algo de la pantalla).

- Escena en `<canvas>` (2D) o SVG, con gsap para la animación (`import { gsap } from 'gsap'`). Fondo limpio,
  trazo de 2–3 px, colores de la paleta; parecer dibujado a mano está bien (trazos que se van dibujando con
  `stroke-dasharray`/`stroke-dashoffset` en SVG).
- Divide la explicación en 3–6 pasos. Cada paso: un título corto en pantalla, una animación que muestra la idea
  y una frase narrada con `colo.say()`. Botones: ◀ ▶, ⏸ y ↺. Arranca en el paso 1 y espera, o avanza solo con
  la narración si pidió «explícame» (usa un timeline de gsap con `.call(() => colo.say(...))` por paso).
- Ejemplo de red neuronal: capas de círculos conectados; los pesos como grosor de línea; una señal (puntos de
  color) viaja de la entrada a la salida; la salida se ilumina; luego el error vuelve hacia atrás y cambian los
  grosores (backpropagation).
- Lo que se mueve tiene que significar algo. Menos elementos y más claros es mejor que una escena llena.
- Tamaño: el canvas ocupa el ancho (`width = clientWidth * devicePixelRatio`) y unos 420 px de alto; redibuja al
  cambiar de tamaño.
