# ViennaBar

Barra lateral para Windows 11 (22H2+): explorador de archivos + dropzone + widgets + menú de inicio, como shell aditivo. Ligereza como requisito de diseño: un proceso residente, todo lazy.

**Estado**: **F1 completado (2026-09-02)** — sidebar funcional end-to-end. Ver [docs/arquitectura.md](docs/arquitectura.md) y [docs/f0-spike.md](docs/f0-spike.md) (gates y hallazgos).

## Qué funciona (v0.1)

- AppBar auto-hide en el borde izquierdo, 3 tercios (widgets/árbol/inicio), 23.5 MB RAM idle, 0% CPU
- Árbol del namespace shell expandible (Este equipo, Escritorio, Descargas, Documentos)
- Menú inicio (drawer) con las 122 apps del sistema + **búsqueda por teclado** (filtro en vivo, flechas, Enter)
- Launch real de apps vía `IContextMenu` canónico (Win32, UWP y paneles)
- Dropzone: arrastre de archivos desde el Explorer al stack
- Render Direct2D/DirectWrite (sin GDI+, sin frameworks)

## Decisiones fijadas

| Decisión | Valor |
|---|---|
| Stack | C# .NET 8 NativeAOT + CsWin32 + Direct2D/DirectComposition |
| Target | Windows 11 22H2+ (x64) |
| Menú de inicio | Catálogo propio (AppsFolder + LNK + Windows Search) |
| Modo shell completo (M3) | Sí, F4 opt-in con auto-reversión |
| Skin default | "Vienna Celeste" (acrylic celeste, sheen Aero) |

## Presupuesto de rendimiento (gates F0/F1)

- Core idle: < 25 MB private bytes, 0 % CPU (event-driven)
- Arranque frío: < 350 ms
- Binario: < 10 MB, sin runtime que instalar (NativeAOT)
- Stub de activación (verb/IFEO): < 50 ms

## Estructura

```
src/ViennaBar/              core residente (appbar, UI D2D/DComp, tree, drop, drawer)
src/ViennaBar.Launcher/     wrapper IFEO (solo modo M2)
src/ViennaBar.WidgetHost/   host out-of-proc para widgets de terceros (F5)
tests/                      gates de rendimiento + tests unitarios
docs/                       arquitectura y matriz de cobertura
```

## Modos de integración (todos opt-in, reversibles)

| Modo | Mecanismo | Fase |
|---|---|---|
| M0 | Coexistencia (default) | F1 |
| M1 | Verbs HKCU sobre Directory/Drive/Folder (+ manejo DelegateExecute) | F3 |
| M2 | IFEO Debugger sobre explorer.exe → Launcher; TaskbarAl | F3 |
| M3 | Winlogon Shell (tray, wallpaper, alt-tab propios) | F4 |
