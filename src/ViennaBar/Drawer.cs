using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// StartDrawer — tercio inferior. Colapsado: botón Inicio + pins.
// Expandido: all-programs (desde AppCatalog cache) + search box + power.
internal sealed class Drawer
{
    private const float StartBtnH = 36f;
    private const float SearchH = 26f;
    private const float RowH = 18f;

    private readonly AppCatalog _catalog = new();
    private HWND _hwnd;
    private string _search = "";
    private int _selIdx;                       // selección de teclado
    private int _topRow;                       // scroll virtual (primera fila visible)
    private float _drawerAreaH = 400f;          // F2: métrica real del layout

    public static TextFormatHandle F;
    public static TextFormatHandle FBig;

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _catalog.Attach();
    }

    public void InitText(Renderer r)
    {
        F = r.Text9Handle;
        FBig = r.Text11bHandle;
    }

    public void SetDrawerArea(float h) => _drawerAreaH = h;

    public void FocusSearch() { /* la ventana ya gana foco al activarse con click */ }

    // teclado: WM_CHAR escribe, KEYDOWN navega/ejecuta. true → repintar.
    public bool OnKey(uint msg, WPARAM wparam)
    {
        if (msg == WM_CHAR)
        {
            char c = (char)wparam.Value;
            if (c == 27) return false;                       // ESC lo maneja KEYDOWN
            if (c == '\r' || c == '\n')
            {
                LaunchSelected();
                return true;
            }
            if (c == '\b')
            {
                if (_search.Length > 0) _search = _search[..^1];
                _selIdx = 0; _topRow = 0; _resultsCache = null;
                return true;
            }
            if (!char.IsControl(c) && _search.Length < 64)
            {
                _search += c;
                _selIdx = 0; _topRow = 0; _resultsCache = null;
                return true;
            }
            return false;
        }

        // WM_KEYDOWN
        int vk = (int)wparam.Value;
        var results = CurrentResults();
        switch (vk)
        {
            case 0x1B: // VK_ESCAPE: limpia búsqueda
                if (_search.Length > 0) { _search = ""; _selIdx = 0; _topRow = 0; _resultsCache = null; return true; }
                return false;
            case 0x26: // VK_UP
                if (_selIdx > 0) { _selIdx--; ClampScroll(results.Count); return true; }
                return false;
            case 0x28: // VK_DOWN
                if (_selIdx < results.Count - 1) { _selIdx++; ClampScroll(results.Count); return true; }
                return false;
            // VK_RETURN NO va acá: Enter produce WM_KEYDOWN + WM_CHAR('\r') —
            // manejarlo en ambos = doble launch. Solo WM_CHAR lo lanza.
        }
        return false;
    }

    private void LaunchSelected()
    {
        var results = CurrentResults();
        if (_selIdx >= 0 && _selIdx < results.Count)
            Launch(results[_selIdx]);
    }

    private void ClampScroll(int total)
    {
        int visible = VisibleRows;
        if (_topRow > _selIdx) _topRow = _selIdx;
        if (_selIdx >= _topRow + visible) _topRow = _selIdx - visible + 1;
        if (_topRow < 0) _topRow = 0;
    }

    private int VisibleRows => Math.Max(1, (int)((_drawerAreaH - SearchH - StartBtnH - 12) / RowH));

    // click dentro del área del drawer (coords locales al drawer).
    // Layout: search box [0..SearchH] | filas desde SearchH+2 (calza con Render).
    public void OnClick(int x, int y, int drawerH)
    {
        if (y < SearchH + 2) return; // search box: F2 (IME)

        var results = CurrentResults();
        int idx = (int)((y - SearchH - 2) / RowH) + _topRow;
        if (idx >= 0 && idx < results.Count)
        {
            _selIdx = idx;
            Launch(results[idx]);
        }
    }

    private List<AppCatalog.AppEntry> CurrentResults()
    {
        // cacheada: solo recompute al cambiar search o refrescar catálogo
        if (_resultsCache is null || _cacheSearch != _search)
        {
            _resultsCache = (string.IsNullOrEmpty(_search)
                ? _catalog.Apps
                : _catalog.Search(_search)).Take(400).ToList();
            _cacheSearch = _search;
        }
        return _resultsCache;
    }

    private List<AppCatalog.AppEntry>? _resultsCache;
    private string? _cacheSearch;

    // BHID_SFUIObject = "GetUIObjectOf" del item — ruta canónica para IContextMenu
    private static readonly Guid SfUiObjectGuid = BHID_SFUIObject;

    // "open\0" ANSI persistente (InvokeCommand lee el LPCSTR mucho después
    // del retorno — jamás stackalloc)
    private static readonly nint _openVerbPtr = InitOpenVerb();

    private static nint InitOpenVerb()
    {
        var p = Marshal.AllocHGlobal(5);
        Marshal.WriteByte(p, 0, (byte)'o');
        Marshal.WriteByte(p, 1, (byte)'p');
        Marshal.WriteByte(p, 2, (byte)'e');
        Marshal.WriteByte(p, 3, (byte)'n');
        Marshal.WriteByte(p, 4, 0);
        return p;
    }

    private unsafe void Launch(AppCatalog.AppEntry entry)
    {
        if (entry.Pidl is null || entry.Pidl.Length < 4)
        {
            Console.WriteLine($"[drawer] sin pidl snapshot: {entry.Name}");
            return;
        }

        try
        {
            // 1) AppsFolder IShellFolder (ruta validada en el catálogo)
            Guid iidItem = typeof(IShellItem).GUID;
            var hr = SHGetKnownFolderItem(FOLDERID_AppsFolder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, in iidItem, out var appsObj);
            if (hr.Failed || appsObj is not IShellItem appsItem)
            {
                Console.WriteLine($"[drawer] appsfolder item FAIL");
                return;
            }

            Guid bhid = BHID_SFObject;
            Guid iidFolder = typeof(IShellFolder).GUID;
            appsItem.BindToHandler(default, &bhid, &iidFolder, out var sfObj);
            if (sfObj is not IShellFolder appsFolder)
            {
                Console.WriteLine($"[drawer] appsfolder bind FAIL");
                return;
            }

            // 2) PIDL child restaurado del snapshot del catálogo (válido: salió
            //    de la enumeración del propio AppsFolder — sin parse intermedio)
            var child = (ITEMIDLIST*)Marshal.AllocCoTaskMem(entry.Pidl.Length);
            Marshal.Copy(entry.Pidl, 0, (nint)child, entry.Pidl.Length);

            try
            {
                // 3) IContextMenu del item: QueryContextMenu (inicializa el handler
                //    — InvokeCommand sin Query previo → E_INVALIDARG) + Invoke "open"
                Guid iidMenu = typeof(IContextMenu).GUID;
                appsFolder.GetUIObjectOf(default, 1, &child, &iidMenu, null, out var menuObj);
                if (menuObj is not IContextMenu menu)
                {
                    Console.WriteLine($"[drawer] sin IContextMenu: {entry.Name}");
                    return;
                }

                // QueryContextMenu con menú dummy: el handler registra sus comandos
                var hmenu = CreatePopupMenu();
                menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                _ = DestroyMenu(hmenu);

                // struct CHICO (CMINVOKECOMMANDINFO): los handlers validan cbSize
                var ici = new CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    lpVerb = (PCSTR)(byte*)_openVerbPtr, // "open" ANSI
                    nShow = 5,                           // SW_SHOW
                };
                menu.InvokeCommand(&ici);
                Console.WriteLine($"[drawer] launch OK: {entry.Name}");
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)child);
            }
        }
        catch (COMException cex)
        {
            Console.WriteLine($"[drawer] COM FAIL 0x{cex.HResult:X}: {entry.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[drawer] launch error {entry.Name}: {ex}");
        }
    }

    // orbe Vienna: círculo exterior glass + núcleo brillante (estilo Aero)
    private static void DrawOrb(RenderCtx ctx, float cx, float cy)
    {
        // halo exterior (glass celeste)
        ctx.FillEllipse(Skin.SheenTop, cx, cy, 11f, 11f);
        // núcleo azul
        ctx.FillEllipse(Skin.Btn, cx, cy, 8.5f, 8.5f);
        // highlight superior (reflejo)
        ctx.FillEllipse(Skin.White, cx - 2.5f, cy - 3.5f, 3.2f, 2.4f);
    }

    public void Render(RenderCtx ctx, int x, int y, int w, int h, bool open)
    {
        // botón Inicio: orbe Vienna (círculo azul con highlight) + texto
        float by = y + h - StartBtnH - 4;
        ctx.FillRect(Skin.Btn, 4, by, w - 8, StartBtnH);

        // orbe: círculo blanco semitransparente con núcleo (sin ellipses API en
        // RenderCtx aún → aproximación con 3 rects concéntricos suaves)
        float cx = 16f, cy = by + StartBtnH / 2f;
        DrawOrb(ctx, cx, cy);

        // texto "Inicio" desplazado por el orbe
        ctx.Text("Inicio", FBig, Skin.White, 34, by + (StartBtnH - 18) / 2);

        if (!open) return;

        float dy = y + 4;
        // search box con el texto real + caret
        ctx.FillRect(Skin.Search, 4, dy, w - 8, SearchH);
        ctx.Line(Skin.Divider, 4, dy, w - 4, dy);
        ctx.Line(Skin.Divider, 4, dy + SearchH, w - 4, dy + SearchH);
        ctx.Line(Skin.Divider, 4, dy, 4, dy + SearchH);
        ctx.Line(Skin.Divider, w - 4, dy, w - 4, dy + SearchH);
        string caret = "|";
        ctx.Text(string.IsNullOrEmpty(_search) ? "Buscar... " + caret : _search + caret, F, Skin.Text, 10, dy + 4);
        dy += SearchH + 2;

        // filas con scroll + selección
        var results = CurrentResults();
        int visible = VisibleRows;
        int first = Math.Min(_topRow, Math.Max(0, results.Count - 1));
        int last = Math.Min(first + visible, results.Count);
        for (int i = first; i < last; i++)
        {
            float ry = dy + (i - first) * RowH;
            if (i == _selIdx)
                ctx.FillRect(Skin.Sel, 6, ry - 1, w - 12, RowH);
            ctx.Text(results[i].Name, F, i == _selIdx ? Skin.Text : Skin.Text, 8, ry, w - 20);
        }

        // scrollbar minimal si hay overflow
        if (results.Count > visible)
        {
            float trackH = visible * RowH;
            float thumbH = Math.Max(12f, trackH * visible / results.Count);
            float thumbY = dy + (trackH - thumbH) * first / Math.Max(1, results.Count - visible);
            ctx.FillRect(Skin.Divider, w - 6, dy, 2, trackH);
            ctx.FillRect(Skin.Text, w - 6, thumbY, 2, thumbH);
        }
    }

    public void Dispose() => _catalog.Dispose();
}
