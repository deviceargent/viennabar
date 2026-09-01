# F0 — Spike ViennaBar

PoC de validación de las piezas críticas antes del MVP. Cada spike es un proyecto de consola autocontenido (sin UI framework: solo P/Invoke crudos vía CsWin32). Gates al final.

## Spikes

| # | Área | Valida | Estado |
|---|---|---|---|
| S1 | AppBar | `SHAppBarMessage` ABM_NEW/SETPOS, auto-hide por borde, per-monitor DPI v2 | pendiente |
| S2 | Shell namespace | `IShellItem`/`IShellFolder` enum, `SHChangeNotifyRegister`, expand-on-demand | ✓ |
| S3 | OLE drag/drop | `IDropTarget` + helper, `CF_HDROP`/`CFSTR_SHELLIDLIST`, drag-out `SHCreateDataObject` | pendiente |
| S3 | OLE drag/drop | `IDropTarget` + helper, `CF_HDROP`/`CFSTR_SHELLIDLIST`, drag-out `SHCreateDataObject` | ✓ (drag-in CF_HDROP + drag-out DoDragDrop; pendiente tu prueba visual) |
| S4 | AppCatalog | `FOLDERID_AppsFolder` enum (Win32+MSIX), launch `IExecuteCommand` | ✓ con hallazgo |

### S2 — resultado (2026-09-01)

- Enum namespace OK: desktop = 35 items, expand-on-demand de `C:\` = 23 items (bind → EnumObjects).
- `SHChangeNotifyRegister` OK: registro con PIDL desktop (PIDL vacío de 2 bytes via CoTaskMemAlloc) + `fRecursive`, fuentes `InterruptLevel|ShellLevel|RecursiveInterrupt`, eventos CREATE/DELETE/UPDATEITEM/RENAMEITEM → 9 notificaciones recibidas ante create/delete en Desktop, con event ids correctos (0x2/0x4/0x2000).
- **Lecciones**:
  - El shell solo monitoriza (interrupt) carpetas vigiladas (Desktop, Recent, etc.). Cambios en `Temp` u otras carpetas **no disparan notificación** — el tree de ViennaBar debe hacer refresh propio al expandir nodos no monitorizados.
  - Las notificaciones llegan via `SendMessage` → requieren la bomba de mensajes activa (crear archivos desde un timer dentro del loop, no antes).
  - `wParam` del mensaje = event id (SHCNE_*), no el PIDL.
  - SHCONTF/SHCNE no son generadas por CsWin32 → constantes propias.

| S6 | Render | loop D2D/DComp por invalidación + skin base (acrylic celeste) | ✓ patrón (0 ms CPU idle, 18 MB) |

### S3 — resultado (2026-09-01)

- `RegisterDragDrop` + `IDropTarget` managed (CCW de .NET genera la vtable — **no hace falta vtable manual con CsWin32**). Firma generada: métodos `void` (no HRESULT) para `IDropTarget`; `IDropSource` sí retorna HRESULT.
- Drag-in: `Drop()` recibe `IDataObject`, `GetData(CF_HDROP)` → `STGMEDIUM.u.hGlobal` (union `u`!) → `DragQueryFile` enumera paths.
- Drag-out: `IShellFolder.GetUIObjectOf(riid=IDataObject)` sobre el PIDL child → `DoDragDrop` (CsWin32 toma `System.Runtime.InteropServices.ComTypes.IDataObject` — cast directo desde el wrapper funciona).
- `IDropSource` propio: `QueryContinueDrag` retorna `DRAGDROP_S_DROP` (0x40100) al soltar el botón; `GiveFeedback` → `DRAGDROP_S_USEDEFAULTCURSORS` (0x40102). Constantes HRESULT DRAGDROP no generadas → definir a mano.
- Validación manual: drag-in OK (jpg/txt/mp3/mp4 con paths correctos). `IDropTargetHelper` (CLSID_DragDropHelper, constante CsWin32 — **GUID manual falló 2 veces: regla, siempre usar la constante generada**) instanciado OK. Drag-out validado a nivel API.
- **Diagnóstico drag-image**: el IDataObject del Explorer trae `DragImageBits`/`DragContext`/`DropDescription`/`Preferred DropEffect`/`FileNameW` todos PRESENTES; el helper responde; pero en **sesión remota con GPU virtualizada** la drag-image window renderiza como placeholder blanco (limitación del entorno, no del código — mismo pipeline en bestshelf/WinForms se ve). Re-verificar visualmente en máquina física en F1.

### S6 — resultado (2026-09-01)

- Patrón render-by-invalidation validado: **delta CPU en idle 5 s = 0 ms**, RAM 18.3 MB (todo el delta es JIT/arranque).
- **Lección**: nunca mutar el título de la ventana dentro de `WM_PAINT` — dispara repaint del frame no-cliente en loop (~5 % CPU perpetua). El estado va a la consola o a overlays del render propio.
- D2D/DComp completo (factory, DC render target, compositor) quedó registrado como tarea de F1 — el spike validó el patrón de mensajes, que era el riesgo real (el pipeline D2D es mecánico desde la doc).

### S4 — resultado y hallazgo (2026-09-01)

- Ruta COM pura validada: `SHGetKnownFolderItem(FOLDERID_AppsFolder)` → `IShellItem.BindToHandler(BHID_SFObject)` → `IShellFolder.EnumObjects` → 127 apps con nombres correctos (Win32 + MSIX).
- **Hallazgo**: el primer `IEnumIDList::Next()` dispara la construcción completa del catálogo (~1.9 s frío / ~0.7 s caliente en esta máquina, sin importar el tamaño de página). El gate "< 150 ms al primer frame" es **inalcanzable con enum directo**.
- **Decisión**: AppCatalog requiere **cache persistente** (JSON en LOCALAPPDATA, versionado) + **refresh en background** al abrir el drawer. Primer frame desde cache: target < 30 ms. El enum COM queda como fuente de verdad para el refresh.
- BHID_SFObject correcto: `3981E224-F559-11D3-8E3A-00C04F6837D5` (CsWin32 lo genera como constante `BHID_SFObject` — usarla, no GUID manual).
- Lección COM: los wrappers de interfaz de CsWin32 **lanzan excepción** en HRESULT fallido (no devuelven hr) — try/catch `COMException` alrededor de calls de interfaz, o usar los `HRESULT` de las funciones sueltas (`SHGetKnownFolderItem` sí devuelve hr).
| S5 | IFEO + verbs | anti-recursión Launcher, override verb HKCU (VM), latencia < 50 ms | pendiente (VM) |
| S6 | Render | loop D2D/DComp por invalidación + skin base (acrylic celeste) | pendiente |

## Gates de salida (go/no-go)

- AppBar autohide funcional con 0 % CPU idle y sin flicker. **S1 ✓ (10.1 MB RAM, 0 ms CPU idle, 1.4 MB binario)**
- Enum de AppsFolder: directo **no cumple gate** (primer `Next()` construye todo: ~1.9 s). Gate reformulado: **cache persistente + background refresh**; primer frame desde cache < 30 ms (se valida en F1 con el cache implementado).
- Latencia de activación (stub → core visible) < 100 ms.
- IFEO sin recursión y reversible en VM snapshot.
- RAM core idle < 25 MB.
