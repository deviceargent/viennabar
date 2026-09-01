# F0 — Spike ViennaBar

PoC de validación de las piezas críticas antes del MVP. Cada spike es un proyecto de consola autocontenido (sin UI framework: solo P/Invoke crudos vía CsWin32). Gates al final.

## Spikes

| # | Área | Valida | Estado |
|---|---|---|---|
| S1 | AppBar | `SHAppBarMessage` ABM_NEW/SETPOS, auto-hide por borde, per-monitor DPI v2 | pendiente |
| S2 | Shell namespace | `IShellItem`/`IShellFolder` enum, `SHChangeNotifyRegister`, expand-on-demand | pendiente |
| S3 | OLE drag/drop | `IDropTarget` + helper, `CF_HDROP`/`CFSTR_SHELLIDLIST`, drag-out `SHCreateDataObject` | pendiente |
| S4 | AppCatalog | `FOLDERID_AppsFolder` enum (Win32+MSIX), launch `IExecuteCommand` | ✓ con hallazgo |

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
