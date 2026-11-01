---
name: mapas
description: lugares, rutas, viajes y datos por región en un mapa real (MapLibre + OpenFreeMap, sin clave)
---
# Mapas

```js
import maplibregl from 'maplibre-gl';
const dark = matchMedia('(prefers-color-scheme: dark)').matches;
const map = new maplibregl.Map({ container: 'map', center: [-46.63, -23.55], zoom: 10, attributionControl: { compact: true },
  style: `https://tiles.openfreemap.org/styles/${dark ? 'dark' : 'positron'}` });
map.addControl(new maplibregl.NavigationControl({ showCompass: false }));
map.on('load', () => {
  // Fronteras en disputa fuera (dibujan rayas raras en América del Sur).
  for (const l of map.getStyle().layers) if (/disputed|boundary_3/.test(l.id)) map.setLayoutProperty(l.id, 'visibility', 'none');
  new maplibregl.Marker({ color: colo.color(0) }).setLngLat([-46.63, -23.55]).setPopup(new maplibregl.Popup().setText('São Paulo')).addTo(map);
});
```
- En html, antes del mapa: `<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1/dist/maplibre-gl.css">`.
- OJO: MapLibre usa [lng, lat] (al revés que Leaflet). `#map` con altura fija (420–520 px) y
  `border-radius: var(--radius); overflow: hidden`.
- Coordenadas reales: búscalas (web read de Wikipedia, o desde el JS
  `https://nominatim.openstreetmap.org/search?format=json&q=...`). Si son aproximadas, dilo.
- Varios puntos: marcadores numerados si hay orden; al final `map.fitBounds(bounds, { padding: 60 })`.
- Rutas: una línea (`addSource` geojson LineString + `addLayer` type line con `line-color` de la paleta) entre
  puntos es ilustrativa, no un trayecto real; no inventes distancias ni tiempos de viaje.
- Viaje animado: los puntos aparecen uno a uno, la línea se va completando y una tarjeta al lado muestra cada
  parada (`map.flyTo` suave entre paradas). Botón para repetir.
- Datos por región: GeoJSON real (IBGE, Natural Earth por URL) pintado con `fill-color` en escala; leyenda al
  lado.
