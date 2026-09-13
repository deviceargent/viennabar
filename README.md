# ViennaBar

Barra lateral estilo **Windows Vista/Win7** para Windows 11. Un proceso residente, liviano, que reemplaza/aumenta la navegación del escritorio: árbol de carpetas, dropzone para archivos, widgets de sistema, menú de inicio y selector de temas.

**Stack**: C# `.NET 8` + **NativeAOT** (binario nativo, sin runtime) + **CsWin32** + **Direct2D/DirectWrite** puro. Sin GDI+, sin WPF, sin frameworks de UI, sin DWM blur.

---

## Características

- **AppBar auto-hide** en el borde izquierdo, con 3 tercios: widgets · árbol de carpetas · menú de inicio (drawer).
- **Árbol del namespace shell** expandible (Este equipo, Escritorio, Descargas, Documentos), con **iconos de carpeta**, thumbnails de imágenes y vista previa al pasar el mouse.
- **Buscador de carpetas** user-scope (perfil + known folders), con aliases en español (`Descargas` = Downloads) y matching insensible a acentos.
- **Dropzone**: arrastrar archivos desde el Explorer al stack, con **drag-out** de vuelta al escritorio (D2D/DirectWrite).
- **Widgets**: reloj, CPU, RAM, discos, portapapeles (texto + imágenes) con historial.
- **Menú de inicio** (drawer) con catálogo de apps + búsqueda por teclado + **pins** + **botones de energía** (apagar/reiniciar/suspender/bloquear con confirmación).
- **Rueda de color HSV** para personalizar: listón, barras de widget, fondo y botón de inicio (persistido en `config.json`).
- **Temas**: `default` (celeste), `ViennaNight` (azul noche) y `ViennaDusk` (atardecer), rotados con `Ctrl+Shift+T`.
- **Redondez, gradiente de fondo, accent bar** — pulido visual D2D puro, sin costo de composición.

---

## Build

```powershell
# requiere .NET 8 SDK + toolchain nativo (VC++/Windows SDK)
.\build.ps1            # compila (debug)
.\build.ps1 -Publish   # publica el exe AOT (~2 MB, PublishAot=true)
```

> El `build.ps1` inyecta `LIB`/`PATH` de VC++/SDK que NativeAOT requiere (sin eso `link.exe` falla con LNK1104 ucrt.lib).

Ejecutar:

```powershell
.\src\ViennaBar\bin\Release\net8.0-windows\ViennaBar.exe
```

Tests headless:

```powershell
dotnet run --project src\ViennaBar.Shell.Tests -c Release -- headless
```

---

## Configuración

`%APPDATA%\ViennaBar\config.json` (hot-reload):

| Clave | Descripción |
|---|---|
| `width`, `revealMs`, `hideMs` | geometría y tiempos del appbar |
| `skin` | tema activo (`default` / `ViennaNight` / `ViennaDusk`) |
| `glassOverlay` | overlays semitransparentes "falso glass" on/off |
| `accent` / `barfill` / `startbtn` / `bgoverride` | overrides de color de la rueda (hex `#RRGGBB`) |
| `Pins` | carpeta fijadas del drawer colapsado |

Skins empaquetados: `%APPDATA%\ViennaBar\skins\<nombre>\skin.json`.

---

## Arquitectura

```
src/ViennaBar/              core residente (appbar, UI D2D/DWrite, tree, drop, drawer, rueda de color)
src/ViennaBar.Shell/        namespace shell y helpers COM (handles nint, AOT-safe)
src/ViennaBar.Gfx/          superficie WIC (carga de imágenes/logo del skin)
src/ViennaBar.Launcher/     wrapper IFEO (modo M2)
src/ViennaBar.Shell.Tests/  suite headless (124 pass)
src/ViennaBar.Spike.*/      spikes de exploración (no forman parte del binario)
```

Docs: [arquitectura](docs/arquitectura.md), [matriz de cobertura](docs/matriz-cobertura.md), [gates F0/F1](docs/f0-spike.md).

---

## Notas técnicas

- **AOT-total**: sin `System.Text.Json` sobre tipos anónimos (serialización a mano), sin COM marshaling (`allowMarshaling=false`, vtables crudas).
- **Render por invalidación** D2D; brushes/bitmaps cacheados con recreación lazy ante device-lost.
- **Colores no-token**: `BrushFor(ARGB)` cachea brushes por color exacto (los overlays/alpha no se pintan con el color del texto).

## Licencia

MIT