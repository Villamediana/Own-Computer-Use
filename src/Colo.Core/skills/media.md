---
name: media
description: vídeos de YouTube, imágenes, carruseles y páginas mezcladas con texto
---
# Vídeos, imágenes y mezcla

- Vídeos: búscalos con web videos y ponlos con
  `<iframe src="https://www.youtube-nocookie.com/embed/ID" allow="autoplay; encrypted-media; picture-in-picture" allowfullscreen style="width:100%;aspect-ratio:16/9;border:0;border-radius:var(--radius)"></iframe>`.
  Uno grande o una lista de 3–4 con miniatura (`https://i.ytimg.com/vi/ID/hqdefault.jpg`), título, canal y
  duración; al hacer clic se carga en el reproductor grande.
- Imágenes: URL reales (web images, o las de la página que leíste). Galería o carrusel: una imagen grande con
  ◀ ▶ y teclas de flecha, miniaturas abajo, pie con la descripción y el autor. `object-fit: cover` en las
  miniaturas, `contain` en la grande.
- Mezcla (un tema explicado): título, un párrafo, la imagen o el vídeo, puntos clave en .card dentro de .grid,
  y enlaces a las fuentes con `colo.open(url)`.
- Letras de canciones, artículos enteros, PDF: no los copies en el código; stage page con la URL de la fuente.
