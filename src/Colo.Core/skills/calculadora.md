---
name: calculadora
description: calculadoras, conversores, simuladores y comparaciones con controles que funcionan
---
# Calculadoras y comparaciones

- Controles reales: `<label>` + `<input type="number">`/`range`/`select`, resultado en `<output role="status">`
  que se recalcula al cambiar (evento `input`). Valores iniciales con sentido (los que dijo el usuario).
- Valida: `valueAsNumber`, `Number.isFinite`, límites, divisores ≠ 0. Nunca muestres NaN ni Infinity; si falta un
  dato, dilo en el resultado. Formatea con `Intl.NumberFormat('pt-BR', {...})` (moneda, %, unidades).
- Muestra la fórmula y los supuestos en texto pequeño (tasa, fecha de la cotización usada, impuestos).
- Comparar opciones: tabla con los criterios en filas y las opciones en columnas, la mejor por criterio
  resaltada; si hay pesos, sliders que reordenan el total.
- Conversiones con tasa real (web primero) y su fecha; simuladores (financiamiento, consumo, tiempo) con un
  gráfico pequeño que se actualiza.
- Botón «Restablecer» que vuelve a los valores iniciales.
