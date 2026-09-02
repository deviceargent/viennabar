# ViennaBar — notas de desarrollo en esta máquina

## Build
- Siempre compilar con `.\build.ps1` (inyecta `LIB`/`PATH` de VC++/SDK que NativeAOT requiere; sin eso `link.exe` falla con LNK1104 ucrt.lib).
- `dotnet` no está en PATH de sesiones nuevas: usar `C:\Program Files\dotnet\dotnet.exe`.
- `.\build.ps1 -Publish` publica el spike AppBar AOT. Pasar `-Project src\<spike>` para otro.

## Reglas CsWin32 aprendidas (S1)
- TFM obligatorio `net8.0-windows` + `<PlatformTarget>x64</PlatformTarget>` o el generator no emite nada.
- Firmas friendly del generado: `BeginPaint(hwnd, out ps)`, `FillRect(hdc, RECT*, HBRUSH)`, `SHAppBarMessage(ABM_NEW, ref abd)` — ver el generado en `obj\...\generated\` con `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` si hay dudas de firma.
- `WNDPROC`: función estática cast `(WNDPROC)WndProc` (NO `[UnmanagedCallersOnly]`, no convertible a delegate).
- Strings nativos: `fixed (char* s = "...")` → `PCWSTR`.
- `LRESULT`: `return default;`, no `return 0;`.
- **GUIDs COM: nunca escribirlos de memoria** — CsWin32 genera las constantes (`BHID_SFObject`, `CLSID_DragDropHelper`, …). Dos GUIDs manuales fallaron en F0 antes de adoptar esta regla.
- **Activación de apps del AppsFolder** (lección F1, costó 5 iteraciones):
  - Los AUMIDs (`{GUID}\app.exe`, `Microsoft.Windows.*`) NO se parsean con `SHCreateItemFromParsingName` ni contra el AppsFolder.
  - Ruta que funciona: PIDL snapshot del enum → `IShellFolder.GetUIObjectOf(IContextMenu)` del AppsFolder.
  - `InvokeCommand` REQUIERE `QueryContextMenu` previo (menú dummy con `CreatePopupMenu`) — sin él: E_INVALIDARG ("Value does not fall within the expected range" del wrapper).
  - `CMINVOKECOMMANDINFO` chico + `lpVerb` = "open" ANSI **persistente** (nada de stackalloc: el handler lee el LPCSTR después del retorno).
  - El E_INVALIDARG de COM llega como `ArgumentException` (no `COMException`) — catch genérico si se quiere log claro.

## Estado
- **F2 en progreso (2026-09-02, noche)**: F2.1a D2D vtables crudas (proyecto `ViennaBar.Gfx`, `allowMarshaling=false` + `InternalsVisibleTo`) → **AOT 2.13 MB, 9.5 MB RAM, 0% CPU**. F2.2 widgets (reloj+CPU+RAM, timer solo visible). F2.3 menú contextual tree (TrackPopupMenu nativo — pendiente prueba visual con 7-Zip). F2.4 skin JSON hot-reload (`%APPDATA%\ViennaBar\skin.json`, FileSystemWatcher → PostMessage WM_APP+2 → ReloadBrushes en hilo UI) + botón Inicio orbe D2D.
- **F2.1b PENDIENTE**: migrar shell COM (IShellFolder/IContextMenu/IDataObject del core) a vtables crudas + CCW manual para `IDropTarget` (dropzone) — es lo único entre nosotros y el AOT completo del exe. Hacer con sesión activa: requiere probar drop+launch en AOT.
- Lecciones vtables CsWin32 (`allowMarshaling=false`): interfaces = structs con `lpVtbl`; el pattern es `IFace.Interface*` + `IFace*` raw para Release; `IComIID.IID_Guid` para los Guids; wrappers tipo extension no existen — llamar los métodos de `Interface` directo. **CsWin32 no escanea subdirectorios** para NativeMethods.txt extra: un assembly por config (por eso `ViennaBar.Gfx` es proyecto aparte). Los tipos generados son `internal` → `InternalsVisibleTo` para consumirlos desde el proyecto principal.
- Docs de arquitectura: `docs/arquitectura.md` (fuente de verdad), `docs/matriz-cobertura.md`, `docs/f0-spike.md` (gates F0/F1 + bloqueo AOT resuelto en F2.1a).
