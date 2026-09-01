# F0 — Spike ViennaBar

PoC de validación de las piezas críticas antes del MVP. Cada spike es un proyecto de consola autocontenido (sin UI framework: solo P/Invoke crudos vía CsWin32). Gates al final.

## Spikes

| # | Área | Valida | Estado |
|---|---|---|---|
| S1 | AppBar | `SHAppBarMessage` ABM_NEW/SETPOS, auto-hide por borde, per-monitor DPI v2 | pendiente |
| S2 | Shell namespace | `IShellItem`/`IShellFolder` enum, `SHChangeNotifyRegister`, expand-on-demand | pendiente |
| S3 | OLE drag/drop | `IDropTarget` + helper, `CF_HDROP`/`CFSTR_SHELLIDLIST`, drag-out `SHCreateDataObject` | pendiente |
| S4 | AppCatalog | `FOLDERID_AppsFolder` enum (Win32+MSIX), launch `IExecuteCommand` | pendiente |
| S5 | IFEO + verbs | anti-recursión Launcher, override verb HKCU (VM), latencia < 50 ms | pendiente (VM) |
| S6 | Render | loop D2D/DComp por invalidación + skin base (acrylic celeste) | pendiente |

## Gates de salida (go/no-go)

- AppBar autohide funcional con 0 % CPU idle y sin flicker.
- Enum de AppsFolder < 150 ms frío (caché caliente < 30 ms).
- Latencia de activación (stub → core visible) < 100 ms.
- IFEO sin recursión y reversible en VM snapshot.
- RAM core idle < 25 MB.
