# ViennaBar — Matriz de cobertura de intercepción

Honestidad de producto: qué se puede interceptar y con qué mecanismo. Fuera de la matriz = no interceptable por diseño (sin inyección de DLL).

| Vector de entrada | M1 (verbs) | M2 (IFEO) | M3 (shell) | Notas |
|---|---|---|---|---|
| Doble clic en carpeta (desktop, apps, diálogos) | ✓ primario | parcial | ✓ | |
| "Abrir ubicación de descarga" desde Edge/Chrome/Firefox | ✓ primario | parcial | ✓ | El browser resuelve el verbo `open` in-proc; IFEO solo cubre spawns nuevos |
| Win+E | ✗ | parcial (solo spawns) | ✓ | En config default (SeparateProcess=0) Win+E se atiende in-process sin spawnear: IFEO es ciego. Verificado 2026-09-04 (3x Win+E, cero invocaciones). Solo spawns frios/`/separate` llegan al launcher |
| Taskbar pinned, `.lnk` a carpetas | ✗ | ✓ | ✓ | |
| `explorer.exe <path>` desde apps | ✗ | ✓ | ✓ | |
| `search-ms:`, `shell::{CLSID}` | ✗ | ✓ | ✓ | |
| `SHOpenFolderAndSelectItems` ("abrir y resaltar") | ✗ | ✗ | ✓ | |
| Autoplay / "abrir dispositivo" | ✗ | ✓ (handler Autorun) | ✓ | |
| Diálogos IFileDialog in-app (abrir/guardar, upload) | ✗ | ✗ | ✗ | Se instancian in-proc en la app host; inyectar DLLs rompe los principios del proyecto. Alternativa: drag-out desde ViennaBar hacia `<input type="file">` / dropzones web |
| Menú Win11 "moderno" (`IExplorerCommand` de apps MSIX) | ✗ | ✗ | ✗ | No hospedable fuera de explorer; los handlers clásicos (7-Zip, WinRAR) sí se hospedan |
