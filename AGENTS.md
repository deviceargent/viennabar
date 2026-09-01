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

## Estado
- F0: S1 ✓ (AppBar auto-hide: 1.4 MB AOT, 10.1 MB RAM, 0% CPU idle). S2–S6 pendientes.
- Docs de arquitectura: `docs/arquitectura.md` (fuente de verdad), `docs/matriz-cobertura.md`, `docs/f0-spike.md`.
