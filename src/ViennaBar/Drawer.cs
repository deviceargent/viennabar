using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectWrite;
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
    private int _selIdx = -1;

    public static IDWriteTextFormat F = null!;
    public static IDWriteTextFormat FBig = null!;

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _catalog.Attach();
    }

    public void InitText(Renderer r)
    {
        F = r.Text9;
        FBig = r.Text11b;
    }

    // click dentro del área del drawer (coords locales al drawer).
    // Layout: search box [0..SearchH] | filas desde SearchH+2 (calza con Render).
    public void OnClick(int x, int y, int drawerH)
    {
        if (y < SearchH + 2) return; // search box: F2 (IME)

        var results = CurrentResults();
        int idx = (int)((y - SearchH - 2) / RowH);
        if (idx >= 0 && idx < results.Count)
        {
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

    public void Render(RenderCtx ctx, int x, int y, int w, int h, bool open)
    {
        // botón Inicio: anclado abajo
        float by = y + h - StartBtnH - 4;
        ctx.FillRect(Skin.Btn, 4, by, w - 8, StartBtnH);
        ctx.Text("Inicio", FBig, Skin.White, w / 2f - 24, by + (StartBtnH - 18) / 2);

        if (!open) return;

        float dy = y + 4;
        ctx.FillRect(Skin.Search, 4, dy, w - 8, SearchH);
        ctx.Line(Skin.Divider, 4, dy, w - 4, dy);
        ctx.Line(Skin.Divider, 4, dy + SearchH, w - 4, dy + SearchH);
        ctx.Line(Skin.Divider, 4, dy, 4, dy + SearchH);
        ctx.Line(Skin.Divider, w - 4, dy, w - 4, dy + SearchH);
        ctx.Text(string.IsNullOrEmpty(_search) ? "Buscar..." : _search, F, Skin.Muted, 10, dy + 4);
        dy += SearchH + 2;

        foreach (var app in CurrentResults())
        {
            if (dy + RowH > y + h - StartBtnH - 8) break;
            ctx.Text(app.Name, F, Skin.Text, 8, dy, w - 20);
            dy += RowH;
        }
    }

    public void Dispose() => _catalog.Dispose();
}
