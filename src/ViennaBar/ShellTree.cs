using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// Namespace provider — árbol del namespace shell con expand-on-demand.
// Raíz: Desktop (PIDL vacío). F1: click en nodo con SFGAO_FOLDER → toggle expand.
internal sealed unsafe class ShellTree : IDisposable
{
    private const uint WM_SH_NOTIFY = WM_APP + 1;

    private HWND _hwnd;
    private IShellFolder _desktop = null!;
    private uint _notifyId;

    // nodos expandidos visibles (path relativo al desktop, F1: jerarquía plana por nivel)
    public readonly List<TreeNode> Roots = new();
    private string? _selected;

    public sealed class TreeNode
    {
        public required string Name;
        public required string ParsingName;
        public bool IsFolder;
        public bool Expanded;
        public byte[]? Pidl;              // child PIDL snapshot (para IContextMenu)
        public readonly List<TreeNode> Children = new();
    }

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        var hr = SHGetDesktopFolder(out _desktop);
        if (hr.Failed) return;

        // raíces: Este equipo + carpetas de usuario comunes
        foreach (var (name, parse) in new[]
        {
            ("Este equipo", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
            ("Escritorio", null),
            ("Descargas", null),
            ("Documentos", null),
        })
        {
            string? path = name switch
            {
                "Escritorio" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Descargas" => GetKnownPath("374DE290-123F-4565-9164-39C4925E467B"),
                "Documentos" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                _ => null,
            };
            var node = new TreeNode { Name = name, ParsingName = path ?? parse!, IsFolder = true, Expanded = false };
            Roots.Add(node);
        }

        // notificaciones de cambio del shell (lección S2: solo carpetas monitorizadas)
        var pidl = (ITEMIDLIST*)CoTaskMemAlloc(2);
        pidl->mkid.cb = 0;
        var entry = new SHChangeNotifyEntry { pidl = pidl, fRecursive = true };
        _notifyId = SHChangeNotifyRegister(_hwnd,
            SHCNRF_SOURCE.SHCNRF_InterruptLevel | SHCNRF_SOURCE.SHCNRF_ShellLevel,
            0x00000002 | 0x00000004 | 0x00002000 | 0x00000001, // CREATE|DELETE|UPDATEITEM|RENAMEITEM
            WM_SH_NOTIFY, 1, in entry);
    }

    private static string? GetKnownPath(string guidStr) =>
        SHGetKnownFolderPath(new Guid(guidStr), 0, default, out var p).Succeeded ? p.ToString() : null;

    private static byte[] SnapshotPidl(ITEMIDLIST* pidl)
    {
        uint size = ILGetSize(pidl);
        var buf = new byte[size];
        Marshal.Copy((nint)pidl, buf, 0, (int)size);
        return buf;
    }

    // click derecho en un nodo → menú contextual del shell + handlers de
    // terceros (7-Zip, WinRAR...) via TrackPopupMenu nativo.
    // Requiere el IShellFolder PADRE del ítem: se re-enumera el padre del
    // nodo y se matchea por PIDL (F3: cache de padres por nodo).
    public static unsafe byte[]? TryGetPidlAt(TreeNode node, int y, int width, int height) => node.Pidl;

    public void OnRightClick(int x, int y, int width, int height)
    {
        var (node, _) = HitTest(y, width, height);
        if (node?.Pidl is null || node.Pidl.Length < 4) return;
        ShowContextMenu(node, x, y);
    }

    private void ShowContextMenu(TreeNode node, int x, int y)
    {
        // folder padre: el nodo raíz que lo contiene (o desktop para raíces)
        TreeNode parent = FindParentOf(Roots, node) ?? node;

        // bind al folder del padre (o desktop si es raíz)
        IShellFolder folder = _desktop;
        if (!ReferenceEquals(parent, node) && !string.IsNullOrEmpty(parent.ParsingName))
        {
            BindFolder(parent.ParsingName, out var sf);
            if (sf is not null) folder = sf;
        }

        // child PIDL restaurado
        var child = (ITEMIDLIST*)Marshal.AllocCoTaskMem(node.Pidl!.Length);
        Marshal.Copy(node.Pidl, 0, (nint)child, node.Pidl.Length);
        try
        {
            Guid iidMenu = typeof(IContextMenu).GUID;
            folder.GetUIObjectOf(default, 1, &child, &iidMenu, null, out var menuObj);
            if (menuObj is not IContextMenu menu) return;

            var hmenu = CreatePopupMenu();
            try
            {
                menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                GetCursorPos(out var ptScreen);

                // TrackPopupMenu nativo: renderiza el menú completo (incluye
                // submenús de 7-Zip/WinRAR/Tortoise) — F3 lo skinnea con D2D
                int cmd = TrackPopupMenu(hmenu,
                    TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
                    ptScreen.X, ptScreen.Y, 0, _hwnd, null);
                if (cmd > 0)
                {
                    // cmd es offset: idCmd=1 → offset 0 (MAKEINTRESOURCEA)
                    var ici = new CMINVOKECOMMANDINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                        lpVerb = (PCSTR)(byte*)(nint)(cmd - 1),
                        nShow = 5,
                    };
                    menu.InvokeCommand(&ici);
                }
            }
            finally { _ = DestroyMenu(hmenu); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ctx] error: {ex.Message}");
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)child);
        }
    }

    private static TreeNode? FindParentOf(List<TreeNode> list, TreeNode target)
    {
        foreach (var n in list)
        {
            foreach (var c in n.Children)
                if (ReferenceEquals(c, target)) return n;
            var deeper = FindParentOf(n.Children, target);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private void BindFolder(string parsingName, out IShellFolder? sf)
    {
        sf = null;
        if (_desktop is null) return;
        if (parsingName.StartsWith("::{"))
        {
            ITEMIDLIST* pidl = null;
            uint attr = 0;
            fixed (char* p = parsingName)
            {
                try { _desktop.ParseDisplayName(default, default, p, null, &pidl, ref attr); }
                catch { pidl = null; }
            }
            if (pidl is null) return;
            Guid iid = typeof(IShellFolder).GUID;
            _desktop.BindToObject(pidl, default, &iid, out var obj);
            ILFree(pidl);
            sf = obj as IShellFolder;
        }
        else
        {
            Guid iidItem = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(parsingName, default, in iidItem, out var itemObj).Succeeded
                && itemObj is IShellItem item)
            {
                Guid bhid = BHID_SFObject;
                Guid iidFolder = typeof(IShellFolder).GUID;
                item.BindToHandler(default, &bhid, &iidFolder, out var sfObj);
                sf = sfObj as IShellFolder;
            }
        }
    }

    // click en el área del tree: localiza nodo por Y → toggle expand
    public void OnClick(int x, int y, int width, int height)
    {
        var (node, _) = HitTest(y, width, height);
        if (node is null) return;
        _selected = node.ParsingName;
        if (node.IsFolder)
        {
            if (!node.Expanded && node.Children.Count == 0)
                Expand(node);
            else
                node.Expanded = !node.Expanded;
        }
        App.Instance?.Invalidate();
    }

    private void Expand(TreeNode node)
    {
        if (_desktop is null) return;
        IShellFolder folder = _desktop;

        if (!string.IsNullOrEmpty(node.ParsingName) && node.ParsingName.StartsWith("::{"))
        {
            ITEMIDLIST* pidl = null;
            uint attr = 0;
            fixed (char* p = node.ParsingName)
            {
                try { _desktop.ParseDisplayName(default, default, p, null, &pidl, ref attr); }
                catch { pidl = null; }
            }
            if (pidl is null) return;
            Guid iid = typeof(IShellFolder).GUID;
            _desktop.BindToObject(pidl, default, &iid, out var obj);
            ILFree(pidl);
            if (obj is IShellFolder sf) folder = sf;
        }
        else if (!string.IsNullOrEmpty(node.ParsingName))
        {
            Guid iidItem = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(node.ParsingName, default, in iidItem, out var itemObj).Succeeded
                && itemObj is IShellItem item)
            {
                Guid iidFolder = typeof(IShellFolder).GUID;
                Guid bhid = BHID_SFObject;
                item.BindToHandler(default, &bhid, &iidFolder, out var sfObj);
                if (sfObj is IShellFolder sf) folder = sf;
            }
        }

        try
        {
            var hr = folder.EnumObjects(default, 0x20 | 0x40 | 0x80, out var en); // FOLDERS|NONFOLDERS|HIDDEN
            if (hr.Failed) return;
            node.Children.Clear();
            while (true)
            {
                ITEMIDLIST* child = null;
                uint fetched = 0;
                hr = en.Next(1, &child, &fetched);
                if (hr != 0 || fetched == 0) break;
                try
                {
                    var sr = default(STRRET);
                    folder.GetDisplayNameOf(child, SHGDNF.SHGDN_NORMAL, &sr);
                    string name = sr.uType == 0 && sr.Anonymous.pOleStr.Value != null
                        ? new string(sr.Anonymous.pOleStr.Value)
                        : string.Empty;
                    uint attrs = 0x20000000; // SFGAO_FOLDER
                    folder.GetAttributesOf(1, &child, ref attrs);
                    bool isFolder = (attrs & 0x20000000) != 0;

                    // parsing name del child (para re-bind y expand)
                    string parsing = string.Empty;
                    var sr2 = default(STRRET);
                    folder.GetDisplayNameOf(child, SHGDNF.SHGDN_FORPARSING, &sr2);
                    if (sr2.uType == 0 && sr2.Anonymous.pOleStr.Value != null)
                        parsing = new string(sr2.Anonymous.pOleStr.Value);

                    node.Children.Add(new TreeNode
                    {
                        Name = name,
                        ParsingName = parsing,
                        IsFolder = isFolder,
                        Pidl = SnapshotPidl(child),
                    });
                }
                finally { ILFree(child); }
            }
            Marshal.ReleaseComObject(en);
            node.Expanded = true;
        }
        catch { /* carpetas que fallan bind: quedan vacías */ }
    }

    public void Render(RenderCtx ctx, int x, int y, int w, int h)
    {
        float cy = y + 4;
        const float rowH = 18f;

        foreach (var root in Roots)
        {
            if (cy + rowH > y + h) return;
            if (root.Expanded) ctx.FillRect(Skin.Sel, x + 2, cy - 1, w - 8, rowH);
            ctx.Text((root.Expanded ? "- " : "+ ") + root.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
            cy += rowH;
            if (root.Expanded)
            {
                foreach (var c in root.Children)
                {
                    if (cy + rowH > y + h) return;
                    ctx.Text("    " + (c.IsFolder ? (c.Expanded ? "- " : "+ ") : "  ") + c.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
                    cy += rowH;
                }
            }
        }
    }

    private (TreeNode?, int) HitTest(int y, int width, int height)
    {
        const float rowH = 18f;
        float cy = 4;
        foreach (var root in Roots)
        {
            if (y < cy + rowH && y >= cy) return (root, 0);
            cy += rowH;
            if (root.Expanded)
            {
                foreach (var c in root.Children)
                {
                    if (y < cy + rowH && y >= cy) return (c, 0);
                    cy += rowH;
                }
            }
        }
        return (null, 0);
    }

    public void Dispose()
    {
        if (_notifyId != 0) _ = SHChangeNotifyDeregister(_notifyId);
        if (_desktop is not null) Marshal.ReleaseComObject(_desktop);
    }
}
