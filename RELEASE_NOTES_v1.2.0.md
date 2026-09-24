# VictoryTool v1.2.0

Release date: 2026-09-24

Una actualización centrada en que editar personajes, guardar cambios y crear
mods resulte más sencillo y fiable.

Desde v1.0.0, esta versión reúne mejoras de estabilidad, previsualización,
exportación y distribución.

## Lo más destacado

- Los cambios de apariencia se conservan mejor al clonar, editar y volver a
  guardar un personaje.
- Las previsualizaciones muestran de forma más coherente la cabeza, el cuerpo,
  el uniforme y los retratos.
- La exportación de personajes y mods es más estable, también cuando se crean
  varios personajes a la vez.
- Se han reducido los casos de personajes duplicados, desaparecidos o con
  cambios que no llegaban al juego.
- Mejor compatibilidad con personajes y uniformes personalizados.
- Mejor gestión de entregas, nombres localizados y datos incompletos.
- Los errores son más claros y la aplicación guarda un registro de diagnóstico
  para facilitar la solución de problemas.
- Las compilaciones para Windows son más fiables, especialmente mediante
  PowerShell.

## Distribución

- macOS Apple Silicon: aplicación `VictoryTool.app` autocontenida.
- Windows x64: ejecutable único autocontenido.
- No es necesario distribuir DLLs separadas junto a la aplicación.
- Se incluye el nuevo icono de VictoryTool.

## Importante

La herramienta valida los archivos generados antes de exportarlos, pero cada
mod debe probarse en el juego con la versión de datos correspondiente. Si algo
falla, conserva el registro de diagnóstico para adjuntarlo al informe.
