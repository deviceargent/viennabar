using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar.ShellNative;

// Facade del shell con vtables crudas (AOT-total). API OPACA: los handles
// COM viven como nint â€” el core nunca toca tipos COM (evita colisiones de
// tipos Windows.Win32 entre assemblies con CsWin32).
// internal + InternalsVisibleTo("ViennaBar").
internal static unsafe class ShellNative
{
    public const uint SHCONTF_FOLDERS = 0x20;
    public const uint SHCONTF_NONFOLDERS = 0x40;
    public const uint SHCONTF_INCLUDEHIDDEN = 0x80;

    public static readonly Guid AppsFolderId = new(0x1E87508D, 0x89C2, 0x42F0, 0x8A, 0x7E, 0x64, 0x5A, 0x0F, 0x50, 0xCA, 0x58);
    public static readonly Guid DesktopId = new(0xB4BFCC3A, 0xDB2C, 0x424C, 0xB0, 0x29, 0x7F, 0xE9, 0x9A, 0x87, 0xC6, 0x41);

    // ---------- fundamentals ----------
    public static nint GetDesktopFolder()   // nint = IShellFolder* handle (0 = fail)
    {
        IShellFolder* p = null;
        return TryGetDesktopFolder(out p).Succeeded ? (nint)p : 0;
    }

    public static HRESULT TryGetDesktopFolder(out IShellFolder* desktop)
    {
        fixed (IShellFolder** p = &desktop) return SHGetDesktopFolder(p);
    }

    public static HRESULT TryGetKnownFolderItem(in Guid rfid, out IShellItem* item)
    {
        Guid iid = IShellItem.IID_Guid;
        var hr = SHGetKnownFolderItem(in rfid, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null, in iid, out var raw);
        item = hr.Succeeded ? (IShellItem*)raw : null;
        return hr;
    }

    public static HRESULT TryCreateItemFromParsingName(string path, out IShellItem* item)
    {
        Guid iid = IShellItem.IID_Guid;
        var hr = SHCreateItemFromParsingName(path, null, in iid, out var raw);
        item = hr.Succeeded ? (IShellItem*)raw : null;
        return hr;
    }

    public static bool TryBindItemToFolder(IShellItem* item, out IShellFolder* folder)
    {
        try
        {
            Guid bhid = BHID_SFObject;
            Guid iid = IShellFolder.IID_Guid;
            item->BindToHandler(null, in bhid, in iid, out var raw);
            folder = (IShellFolder*)raw;
            return folder is not null;
        }
        catch { folder = null; return false; }
    }

    // ---------- display names (friendly void â†’ catch) ----------
    public static string DisplayName(IShellFolder* folder, ITEMIDLIST* pidl)
    {
        try
        {
            folder->GetDisplayNameOf(in *pidl, SHGDNF.SHGDN_NORMAL, out var sr);
            return StrRetToString(&sr, pidl);
        }
        catch { return string.Empty; }
    }

    public static string ParsingName(IShellFolder* folder, ITEMIDLIST* pidl)
    {
        try
        {
            folder->GetDisplayNameOf(in *pidl, SHGDNF.SHGDN_FORPARSING, out var sr);
            return StrRetToString(&sr, pidl);
        }
        catch { return string.Empty; }
    }

    private static string StrRetToString(STRRET* sr, ITEMIDLIST* pidl)
    {
        var result = (int)sr->uType switch
        {
            0 => sr->Anonymous.pOleStr.Value != null ? new string(sr->Anonymous.pOleStr.Value) : string.Empty,
            1 => StrRetOffset(pidl, sr->Anonymous.uOffset),
            _ => CStrToString(sr->Anonymous.cStr),
        };
        if ((int)sr->uType == 0 && sr->Anonymous.pOleStr.Value != null)
            CoTaskMemFree(sr->Anonymous.pOleStr.Value);
        return result;
    }

    private static string StrRetOffset(ITEMIDLIST* pidl, uint uOffset)
    {
        byte* p = (byte*)pidl + uOffset;
        return System.Text.Encoding.Default.GetString(p, StrLen(p));
    }

    private static string CStrToString(Windows.Win32.__byte_260 cStr)
    {
        // cStr.Value es un fixed-size buffer: el identificador ES byte* ya
        byte* first = cStr.Value;
        return System.Text.Encoding.Default.GetString(first, StrLen(first));
    }

    private static int StrLen(byte* p) { int n = 0; while (p[n] != 0) n++; return n; }

    // ---------- pidl helpers ----------
    public static byte[] SnapshotPidl(ITEMIDLIST* pidl)
    {
        uint size = ILGetSize(pidl);
        var buf = new byte[size];
        Marshal.Copy((nint)pidl, buf, 0, (int)size);
        return buf;
    }

    public static ITEMIDLIST* RestorePidl(byte[] buf)
    {
        var p = (ITEMIDLIST*)CoTaskMemAlloc((nuint)buf.Length);
        Marshal.Copy(buf, 0, (nint)p, buf.Length);
        return p;
    }

    // ---------- enum children ----------
    public sealed class ChildInfo
    {
        public required string Name;
        public required string ParsingName;
        public bool IsFolder;
        public required byte[] Pidl;
    }

    public static List<ChildInfo> EnumChildren(IShellFolder* folder)
    {
        var list = new List<ChildInfo>();
        IEnumIDList* en = null;
        var hr = folder->EnumObjects(default, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, &en);
        if (hr.Failed || en is null) return list;
        try
        {
            while (true)
            {
                ITEMIDLIST* child = null;
                uint fetched = 0;
                hr = en->Next(1, &child, &fetched);
                if (hr != 0 || fetched == 0 || child is null) break;
                try
                {
                    uint attrs = 0x20000000;   // SFGAO_FOLDER
                    try { folder->GetAttributesOf(1, &child, ref attrs); }
                    catch { attrs = 0; }
                    list.Add(new ChildInfo
                    {
                        Name = DisplayName(folder, child),
                        ParsingName = ParsingName(folder, child),
                        IsFolder = (attrs & 0x20000000) != 0,
                        Pidl = SnapshotPidl(child),
                    });
                }
                finally { ILFree(child); }
            }
        }
        finally { _ = en->Release(); }
        return list;
    }

    // ---------- IContextMenu del item ----------
    private static IContextMenu* GetContextMenu(IShellFolder* parent, ITEMIDLIST* child)
    {
        try
        {
            Guid iidMenu = IContextMenu.IID_Guid;
            parent->GetUIObjectOf(default, 1, &child, in iidMenu, out var raw);
            return (IContextMenu*)raw;
        }
        catch (Exception ex) { SLog($"GetContextMenu FAIL: {ex.Message}"); return null; }
    }

    // ---- logging central: ring buffer SIEMPRE, archivo solo con sentinel ----
    // Costo en camino feliz: un Interlocked + store en memoria (cero I/O).
    // El archivo %TEMP%\viennabar-app.log solo se escribe si existe el
    // centinela %TEMP%\viennabar-debug (mismo patron que viennabar-kill).
    // En un crash nativo, CrashDiag vuelca el ring al viennabar-crash.log,
    // asi que la evidencia forense sobrevive sin I/O en steady-state.
    // Hub compartido con el core (InternalsVisibleTo ViennaBar): el core
    // llama DebugLog directo; el shell usa SLog (agrega el tag).
    internal static readonly bool DebugEnabled = CheckDebugSentinel();

    private static bool CheckDebugSentinel()
    {
        try { return System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viennabar-debug")); }
        catch { return false; }
    }

    private static readonly string[] _ring = new string[256];
    private static int _head;   // total escritos (Interlocked)

    internal static void DebugLog(string s)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        int i = System.Threading.Interlocked.Increment(ref _head);
        _ring[(i - 1) & 255] = line;
        if (!DebugEnabled) return;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viennabar-app.log"), line + "\n"); }
        catch { }
    }

    // snapshot ordenado (mas viejo -> mas nuevo) para el crash-log y tests
    internal static string[] DebugSnapshot()
    {
        int total = System.Threading.Volatile.Read(ref _head);
        int n = Math.Min(total, _ring.Length);
        var snap = new string[n];
        int start = total - n;
        for (int k = 0; k < n; k++) snap[k] = _ring[(start + k) & 255] ?? "(null)";
        return snap;
    }

    private static void SLog(string s) => DebugLog("[shell] " + s);

    // ---------- launch (IContextMenu canÃ³nico â€” lecciones F1) ----------
    public static void LaunchByPidl(IShellFolder* parentFolder, byte[] childPidl)
    {
        var child = RestorePidl(childPidl);
        try
        {
            var menu = GetContextMenu(parentFolder, child);
            if (menu is null) return;
            try
            {
                var hmenu = CreatePopupMenu();
                try
                {
                    try
                    {
                        menu->QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                        var ici = new CMINVOKECOMMANDINFO
                        {
                            cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
                            lpVerb = (PCSTR)(byte*)s_openVerb,
                            nShow = 5,
                        };
                        menu->InvokeCommand(in ici);
                    }
                    catch (Exception ex) { Console.WriteLine($"[shell] launch error: {ex.Message}"); }
                }
                finally { _ = DestroyMenu(hmenu); }
            }
            finally { _ = menu->Release(); }
        }
        finally { CoTaskMemFree(child); }
    }

    private static readonly byte* s_openVerb = MakeOpenVerb();
    private static byte* MakeOpenVerb()
    {
        var p = (byte*)Marshal.AllocHGlobal(5);
        p[0] = (byte)'o'; p[1] = (byte)'p'; p[2] = (byte)'e'; p[3] = (byte)'n'; p[4] = 0;
        return p;
    }

    // ---------- menú contextual interactivo ----------
    public static void ShowContextMenu(IShellFolder* parentFolder, byte[] childPidl, HWND hwnd, int screenX, int screenY)
    {
        var child = RestorePidl(childPidl);
        try
        {
            SLog($"ctx: begin (pidl {childPidl.Length}B, screen {screenX},{screenY})");
            var menu = GetContextMenu(parentFolder, child);
            SLog($"ctx: menu ptr={(nint)menu != 0}");
            if (menu is null) return;
            try
            {
                var hmenu = CreatePopupMenu();
                try
                {
                    menu->QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                    SLog("ctx: QueryContextMenu OK, TrackPopupMenu...");
                    int cmd = TrackPopupMenu(hmenu,
                        TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
                        screenX, screenY, 0, hwnd, null);
                    SLog($"ctx: TrackPopupMenu returned cmd={cmd}");
                    if (cmd <= 0) return;
                    var ici = new CMINVOKECOMMANDINFO
                    {
                        cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
                        lpVerb = (PCSTR)(byte*)(nint)(cmd - 1), // MAKEINTRESOURCEA
                        nShow = 5,
                    };
                    menu->InvokeCommand(in ici);
                    SLog("ctx: InvokeCommand returned");
                }
                finally { _ = DestroyMenu(hmenu); }
            }
            finally { _ = menu->Release(); }
        }
        finally { CoTaskMemFree(child); }
    }

    // =============== API OPACA (handles nint para el core) ===============
    // El core usa SOLO esta secciÃ³n: jamÃ¡s toca tipos COM â†’ sin colisiones
    // de tipos Windows.Win32 entre assemblies CsWin32.

    public static nint OpenFolderByParsingName(string parsingName)
    {
        if (parsingName.StartsWith("::{"))
        {
            try
            {
                IShellFolder* desktop = null;
                if (TryGetDesktopFolder(out desktop).Failed) return 0;
                ITEMIDLIST* pidl = null;
                uint attr = 0;
                fixed (char* p = parsingName)
                {
                    desktop->ParseDisplayName(default, null, p, out pidl, ref attr);
                }
                if (pidl is null) return 0;
                try
                {
                    Guid iid = IShellFolder.IID_Guid;
                    desktop->BindToObject(in *pidl, null, in iid, out var raw);
                    return (nint)raw;
                }
                finally { ILFree(pidl); }
            }
            catch { return 0; }
        }

        try
        {
            if (TryCreateItemFromParsingName(parsingName, out var item).Failed) return 0;
            try
            {
                return TryBindItemToFolder(item, out var sf) ? (nint)sf : 0;
            }
            finally { _ = item->Release(); }
        }
        catch { return 0; }
    }

    public static List<ChildInfo> EnumChildren(nint folderHandle)
    {
        if (folderHandle == 0) return new List<ChildInfo>();
        return EnumChildren((IShellFolder*)folderHandle);
    }

    public static void LaunchByPidl(nint folderHandle, byte[] childPidl) =>
        LaunchByPidl((IShellFolder*)folderHandle, childPidl);

    public static void ShowContextMenu(nint folderHandle, byte[] childPidl, nint hwnd, int screenX, int screenY) =>
        ShowContextMenu((IShellFolder*)folderHandle, childPidl, (HWND)hwnd, screenX, screenY);

    public static string DebugTestQueryContextMenu(nint folderHandle, byte[] childPidl) =>
        DebugTestQueryContextMenu((IShellFolder*)folderHandle, childPidl);

    // ---- bisect del hang F2.1b: ingredientes de la app faltantes en consola ----
    public static void InitSta()
    {
        _ = CoInitializeEx(default, Windows.Win32.System.Com.COINIT.COINIT_APARTMENTTHREADED);
    }

    public static void InitOleStaWithWindow()
    {
        _ = CoInitializeEx(default, Windows.Win32.System.Com.COINIT.COINIT_APARTMENTTHREADED);
        _ = OleInitialize();
    }

    public static void ReleaseFolder(nint folderHandle)
    {
        if (folderHandle != 0) _ = ((IShellFolder*)folderHandle)->Release();
    }

    public static nint OpenAppsFolder()
    {
        // ruta S4 validada: known folder item → BindToHandler(BHID_SFObject)
        if (TryGetKnownFolderItem(in AppsFolderId, out var item).Failed) return 0;
        try
        {
            return TryBindItemToFolder(item, out var sf) ? (nint)sf : 0;
        }
        finally { _ = item->Release(); }
    }

    public static string DebugState()
    {
        var sb = new System.Text.StringBuilder();
        IShellFolder* desktop = null;
        var hr = TryGetDesktopFolder(out desktop);
        sb.Append($"desktop: hr=0x{(int)hr:X} ptr={(nint)desktop != 0}; ");
        var apps = OpenAppsFolder();
        sb.Append($"appsFolder: {(apps != 0 ? "OK" : "FAIL")}");
        if (apps != 0)
        {
            sb.Append($"; appsChildren: {EnumChildren(apps).Count}");
            ReleaseFolder(apps);
        }
        return sb.ToString();
    }

    // =============== drop: parse CF_HDROP de un IDataObject* crudo ===============

    // =============== drag-out: IDataObject del shell para paths del stack ===============
    // Crea un IDataObject del shell con CF_HDROP de los paths. Reusa el objeto
    // del shell (SHCreateDataObject con parent null) y le setea el CF_HDROP
    // globalmem: los targets ven un data object "del shell" con todos los
    // formatos derivados que el shell agrega on-demand.
    // Devuelve IDataObject* (AddRef ya tomado por SHCreateDataObject). 0 = fail.
    public static nint CreateDataObjectFromPaths(IList<string> paths)
    {
        const Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS GMEM_MOVEABLE =
            (Windows.Win32.System.Memory.GLOBAL_ALLOC_FLAGS)0x0002;
        if (paths.Count == 0) return 0;

        // HGLOBAL del CF_HDROP: DROPFILES [header][strings...]\0\0
        int chars = 0;
        foreach (var p in paths) chars += p.Length + 1;
        int total = 20 + (chars + 1) * 2;   // DROPFILES (20B) + double-null
        var hglobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)total);
        if (hglobal == 0) return 0;

        bool ok = false;
        var pDrop = (DROPFILES*)GlobalLock(hglobal);
        if (pDrop is not null)
        {
            try
            {
                *pDrop = new DROPFILES { pFiles = 20, fNC = default, pt = default, fWide = (Windows.Win32.Foundation.BOOL)1 };
                char* dst = (char*)((byte*)pDrop + 20);
                foreach (var p in paths)
                {
                    int i = 0;
                    while (i < p.Length) *dst++ = p[i++];
                    *dst++ = '\0';
                }
                *dst = '\0';   // terminator double-null
                ok = true;
            }
            finally { _ = GlobalUnlock(hglobal); }
        }
        if (!ok) { _ = GlobalFree(hglobal); return 0; }

        Guid iidData = Windows.Win32.System.Com.IDataObject.IID_Guid;
        HRESULT hr;
        void* raw = null;
        try
        {
            hr = SHCreateDataObject(null, 0, null, null, &iidData, &raw);
        }
        catch { hr = default; raw = null; }
        if (hr.Failed || raw is null) { _ = GlobalFree(hglobal); return 0; }

        var data = (Windows.Win32.System.Com.IDataObject*)raw;
        try
        {
            var fmt = new FORMATETC { cfFormat = 15, dwAspect = 1, lindex = -1, tymed = (uint)TYMED.TYMED_HGLOBAL };
            var medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_HGLOBAL,
                u = new Windows.Win32.System.Com.STGMEDIUM._u_e__Union { hGlobal = hglobal },
            };
            data->SetData(in fmt, in medium, (Windows.Win32.Foundation.BOOL)1);   // fRelease=1: el objeto toma el HGLOBAL
            return (nint)data;   // ref del SHCreateDataObject pasa al caller
        }
        catch
        {
            _ = data->Release();
            _ = GlobalFree(hglobal);
            return 0;
        }
    }

    // DoDragDrop con todo raw. El dataObj lo crea CreateDataObjectFromPaths,
    // el dropSource es la CCW de DropSourceCcw. Devuelve (hr, effect).
    public static (int hr, uint effect) DragOut(nint dataObj, nint dropSourceUnknown, uint okEffects)
    {
        var data = (Windows.Win32.System.Com.IDataObject*)dataObj;
        var src = (Windows.Win32.System.Ole.IDropSource*)dropSourceUnknown;
        var effect = default(Windows.Win32.System.Ole.DROPEFFECT);
        var hr = DoDragDrop(data, src, (Windows.Win32.System.Ole.DROPEFFECT)okEffects, &effect);
        return ((int)hr, (uint)effect);
    }

    public static void ReleaseDataObject(nint dataObj)
    {
        if (dataObj != 0) _ = ((Windows.Win32.System.Com.IUnknown*)dataObj)->Release();
    }

    // test AOT del paso QueryContextMenu aislado (bisect del hang F2.1b):
    // GetUIObjectOf(IContextMenu) + QueryContextMenu + DestroyMenu. Sin Track.
    public static string DebugTestQueryContextMenu(IShellFolder* parentFolder, byte[] childPidl)
    {
        var child = RestorePidl(childPidl);
        try
        {
            var menu = GetContextMenu(parentFolder, child);
            if (menu is null) return "menu null";
            try
            {
                var hmenu = CreatePopupMenu();
                try
                {
                    menu->QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                    int n = GetMenuItemCount(hmenu);
                    return $"OK: {n} items";
                }
                finally { _ = DestroyMenu(hmenu); }
            }
            finally { _ = menu->Release(); }
        }
        finally { CoTaskMemFree(child); }
    }

    // Devuelve los paths del CF_HDROP (o lista vacÃ­a). El core lo llama desde
    // el CCW IDropTarget con el void* que le entrega OLE.

    public static List<string> PathsFromDataObject(void* dataObjUnknown)
    {
        var paths = new List<string>();
        try
        {
            // QI manual: vtable IUnknown slot 0 = QueryInterface(riid, ppv)
            var unk = (Windows.Win32.System.Com.IUnknown*)dataObjUnknown;
            Guid iidData = Windows.Win32.System.Com.IDataObject.IID_Guid;
            void* pv = null;
            if (unk->QueryInterface(&iidData, &pv) != 0) return paths;
            var data = (Windows.Win32.System.Com.IDataObject*)pv;
            try
            {
                var fmt = new FORMATETC { cfFormat = 15, dwAspect = 1, lindex = -1, tymed = (uint)TYMED.TYMED_HGLOBAL };
                var medium = default(STGMEDIUM);
                try
                {
                    data->GetData(in fmt, out medium);   // friendly: (in FORMATETC, out STGMEDIUM)
                }
                catch
                {
                    return paths;   // sin CF_HDROP
                }
                if (medium.tymed == TYMED.TYMED_HGLOBAL)
                {
                    var hDrop = new HDROP((nint)medium.u.hGlobal.Value);
                    uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < n; i++)
                    {
                        Span<char> buf = stackalloc char[260];
                        fixed (char* p = buf)
                        {
                            _ = DragQueryFile(hDrop, i, p, 260);
                            paths.Add(new string(p));
                        }
                    }
                    ReleaseStgMedium(ref medium);
                }
            }
            finally { _ = data->Release(); }
        }
        catch { /* data object sin CF_HDROP */ }
        return paths;
    }
}
