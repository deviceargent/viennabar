# ViennaBar — Arquitectura

Documento fuente de verdad. Actualizar con cada decisión que cambie el diseño.

## 1. Principios rectores

1. **Aditivo antes que invasivo**: cuatro niveles de integración (M0–M3), cada uno opt-in y reversible.
2. **Un solo proceso residente**, todo lazy: lo que no se ve no se enumera ni se renderiza.
3. **Skins = datos, no código** (JSON + assets). Sin scripting en skins.
4. **Nada irreversible sin backup**: punto de restauración, backup de claves y auto-reversión ante crash-loop.
5. **Hospedar el ecosistema existente**: las extensiones de shell de terceros (7-Zip, WinRAR…) funcionan por contrato COM, sin integraciones por-app.

## 2. Stack (fijado)

- C# .NET 8 NativeAOT + CsWin32 + Direct2D/DirectComposition.
- Consecuencias de NativeAOT: exe único ~10 MB, sin runtime; **no hay plugins in-proc** → widgets de terceros out-of-process por diseño (aislamiento + estabilidad).
- CsWin32 genera P/Invoke en compile-time (cero reflexión, trimming-safe).
- Target: Windows 11 22H2+, x64. (ARM64: backlog F5+.)

## 3. Modelo de procesos

```
ViennaBar.exe              core: appbar + UI + tree + drop + drawer + settings   (residente)
├── ViennaBar.Launcher.exe wrapper IFEO (solo M2), passthrough < 50 ms
├── ViennaBar.WidgetHost.exe  widgets de terceros (lazy; 0 procesos si no hay)
└── watchdog interno       crash-loop → auto-revert de claves → restaurar explorer
```

## 4. Módulos del core

| Módulo | Responsabilidad |
|---|---|
| Shell.Host | Ventana AppBar (`SHAppBarMessage` ABM_NEW/SETPOS), auto-hide por borde con edge-hot-zone, multi-monitor, per-monitor DPI v2 |
| UI engine | Escena retained sobre D2D/DComp; render solo por invalidación; animaciones en el compositor |
| Theme engine | `skin.json` + PNG 9-slice: tokens de color, blur/tint, tipografía, métricas, estados. Hot-reload |
| Namespace provider | `IShellItem`/`IShellFolder` sobre el namespace completo de shell (no solo FS). Virtualización on-demand al expandir, `SHChangeNotifyRegister`, `IShellItemImageFactory` con caché del shell |
| DropEngine | Passthrough: para cada drop, obtener el `IDropTarget` del ítem destino vía `GetUIObjectOf` y pasarle el `IDataObject` original. Parseo de `CF_HDROP`, `CFSTR_SHELLIDLIST`, `CFSTR_FILEDESCRIPTOR`, `CFSTR_PREFERREDDROPEFFECT`. Drag-out vía `SHCreateDataObject`. Drop Stack (congelar ítems con snapshot de formatos para re-drop) |
| AppCatalog | `FOLDERID_AppsFolder` (Win32 + MSIX con AUMID) + carpetas Start Menu + `Search.CollatorDSO` para búsqueda instantánea. Launch vía `IExecuteCommand`/`ShellExecuteEx` |
| StartDrawer | UI del menú (tercio inferior): colapsado = pins; expandido = all-programs + search + power |
| ProtocolRouter | Destino de cada invocación "exploradora"; pipe de activación desde stub (`--open-folder`, `vienna://folder`) |
| ShellExtHost | Compositor de menú propio+terceros (`GetUIObjectOf(IID_IContextMenu)`), deferral de drops, enumerador de overlays (`IShellIconOverlayIdentifier`, async solo nodos visibles), SendTo, budgets (~500 ms en `QueryContextMenu`), blocklist por CLSID |
| IntegrationManager | Aplica/revierte M0–M3, backups, crash-loop detection |
| Settings / Updater | Config hot-reload (`%APPDATA%\ViennaBar\config.json`); updater con validación de firma |

## 5. Layout

```
┌────────────┐ tercio 1: widgets (grid, slots ordenables)
│  widgets   │ ─ divisor arrastrable
├────────────┤
│  tree      │ tercio 2: árbol de carpetas + breadcrumb + filtro
├────────────┤ ─ divisor arrastrable
│  drawer    │ tercio 3: colapsado = pins │ expandido = all-programs + search + power
│ [Inicio]   │ botón de inicio anclado al fondo, siempre visible
└────────────┘ ancho 240–480 px @96dpi, configurable
```

El drawer despliega dentro de su tercio como overlay animado por el compositor.

## 6. Dropzone — semántica

- Drop sobre nodo del árbol → menú mover/copiar/acceso (respeta `PREFERREDDROPEFFECT`); el efecto real lo resuelve el `IDropTarget` del ítem.
- **Drop stack** (flagship): congelar ítems con snapshot de HGLOBALs para re-drop posterior conservando formatos nativos.
- Widgets drop-aware vía acciones registradas; zonas configurables.
- Drag-out desde árbol y stack (hacia apps, navegadores y `<input type="file">`).

## 7. Modos de integración

| Modo | Mecanismo | Notas |
|---|---|---|
| M0 | Coexistencia (default) | Nada se toca. Ocultar taskbar disponible (TaskbarAl) |
| M1 | Override de verbs `HKCU\Software\Classes\{Directory,Drive,Folder}\shell\open\command`, **eliminando `DelegateExecute`** del valor default → `ViennaBar.exe --open-folder "%V"` | Sin admin; HKCU gana el merge. Cubre doble clic y "abrir ubicación" desde navegadores (Edge llama ShellExecute → resuelve el verbo in-proc → nuestro override). Precedente: Directory Opus / XYplorer |
| M2 | IFEO Debugger sobre explorer.exe → `ViennaBar.Launcher.exe`: carpeta → activar core y salir; no-carpeta → passthrough. + handler Autorun propio | Cubre Win+E, taskbar pinned, `explorer.exe <path>`, `search-ms:`, `shell::{CLSID}`. Anti-recursión obligatoria (gate F0), firma Authenticode, no aplicar con Smart App Control activo, < 50 ms |
| M3 | `Winlogon\Shell = ViennaBar.exe` | explorer nunca arranca = única forma de descargar el render de taskbar+inicio. Obliga a: tray propio (clase `Shell_TrayWnd` → `Shell_NotifyIcon`), wallpaper (Progman/WorkerW), Alt+Tab propio, hotkeys Win+E/R/X. SearchHost/ShellHost quedan ocultos, no eliminados |

Blindaje transversal: firma Authenticode, backup de claves + restore point pre-M1/M2/M3, watchdog con auto-revert ante crash-loop, `.reg` de reversión en escritorio.

## 8. Extensiones de terceros (7-Zip, WinRAR, Tortoise, OneDrive…)

Cero integraciones por-app: hospedar las interfaces COM que explorer hospeda.

| Clase | Mecanismo | Efecto |
|---|---|---|
| Context menu handlers | `GetUIObjectOf(IID_IContextMenu)` → `IShellExtInit` + `QueryContextMenu` + `InvokeCommand` | Menú "7-Zip"/"WinRAR" completo, directo (no atrás de "Mostrar más opciones"), skinned |
| Drag-drop handlers | `IDropTarget` del ítem destino (DropEngine passthrough) | Drop sobre `.zip` → handler nativo de WinRAR/7-Zip |
| Icon/thumbnail | `IShellItemImageFactory` | Gratis desde F1 |
| Overlays | `ShellIconOverlayIdentifiers` → `IsMemberOf` async, solo nodos visibles | Badges OneDrive/Git en el tree |
| "Enviar a" | Enumeración propia de SendTo | Entrada estándar en el menú |

Riesgos: handler que crashea → watchdog + blocklist por CLSID + surrogate out-of-proc (F5); handler lento → budget ~500 ms → submenú "más…" async; bitness x64 (ARM64 en backlog).

## 9. Widgets

- Built-in in-proc: reloj/calendario, clima, now-playing (SMTC), CPU/RAM/red (PDH), espacio en discos, portapapeles (`AddClipboardFormatListener`), drop stack.
- Lifecycle lazy: pausa por oclusión, timers coalescidos.
- Terceros: manifiesto con **capacidades declarativas** (fs.read, net, clipboard…), `WidgetHost.exe` out-of-proc, IPC named pipes JSON, buffer compartido compuesto por el core. Capacidad por defecto: ninguna.

## 10. Skins — "Vienna Celeste"

- Formato: `skin.json` + `assets/*.png` 9-slice; tokens de paleta, blur, tipografía, métricas, estados; hot-reload.
- Default: acrylic celeste (`DWMWA_SYSTEMBACKDROP_TYPE`) + sheen Aero en gradiente D2D + inner highlight 1 px; drawer en Mica; fallback sólido sin composición (RDP/batería).

## 11. Riesgos y mitigaciones

| Riesgo | Mitigación |
|---|---|
| IFEO: AV / SAC / updates | firma, detectar SAC, auto-revert, M2 opt-in |
| Claves internas de Win11 cambian | detección de build, degradar a M1/M0 |
| Anti-recursión IFEO | spike F0 con gate de cierre |
| Costo del UI engine propio | spike F0 decide go/no-go |
| Complejidad OLE drag/drop | spike F0 con helper oficial |
| Crash del core | watchdog + widgets aislados + auto-revert |
| Handler de tercero crashea/lento | blocklist CLSID, budgets, surrogate (F5) |

## 12. Roadmap y gates

| Fase | Contenido | Criterio de salida |
|---|---|---|
| F0 (spike) | PoC: appbar autohide, OLE drag in/out, enum AppsFolder, IFEO anti-recursión, override verb + "Mostrar en carpeta" de Edge (< 100 ms percibido), render loop DComp + skin base, handlers 7-Zip/WinRAR en VM | perf gates medidos; go/no-go stack |
| F1 (MVP, M0) | sidebar + árbol shell + drop + drawer básico + Vienna Celeste | core < 25 MB, arranque < 350 ms |
| F2 | 5 widgets built-in, menú compuesto + drop deferral (criterios: 7-Zip/WinRAR), skin packaging, settings | — |
| F3 | M1 verbs + M2 IFEO + TaskbarAl | reversión probada en VM snapshot |
| F4 | M3 shell completo (tray, wallpaper, alt-tab, overlays) opt-in | crash-loop auto-revert probado |
| F5 | SDK widgets, surrogate handlers, docs, releases firmadas (WiX, no MSIX) | — |

## 13. Registro de decisiones

- 2026-09-01: Stack = C# NativeAOT + D2D/DComp (sobre WPF/Rust/C++). Target Win11 22H2+ only. M3 incluido como F4 opt-in. Menú de inicio = catálogo propio (Open Shell descartado v1, posible passthrough futuro).
- 2026-09-01: M1 es el mecanismo primario para navegadores ("abrir ubicación de descarga"): el verbo se resuelve in-proc en Edge, IFEO solo cubre spawns nuevos. Diálogos IFileDialog in-proc de apps: no interceptables por diseño (inyección de DLL fuera de principios); alternativa = drag-out hacia `<input type="file">`.
- 2026-09-01: DropEngine definido como passthrough a `IDropTarget` por ítem (7-Zip/WinRAR drop handlers funcionan sin código dedicado). ShellExtHost agregado al core.
