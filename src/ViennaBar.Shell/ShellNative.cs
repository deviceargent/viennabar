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

    // avisa al shell que los verbs/asociaciones cambiaron (obligatorio tras
    // Apply/Revert M1: sin esto Explorer sigue usando la cache)
    public static void NotifyAssocChanged()
    {
        try { SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null); }
        catch { }
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

    // =============== drop deferral: mover/copiar via SHFileOperationW ===============
    // P/Invoke crudo (la struct lleva char*): el IDataObject ya se parseo a
    // paths en el OnDrop, asi que el deferral trabaja con strings.
    public const uint FO_MOVE = 1;
    public const uint FO_COPY = 2;
    public const ushort FOF_ALLOWUNDO = 0x40;
    public const ushort FOF_SILENT = 0x4;
    public const ushort FOF_NOCONFIRMATION = 0x10;
    public const ushort FOF_NOERRORUI = 0x400;
    public const ushort FOF_NOCONFIRMMKDIR = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public char* pFrom;
        public char* pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public void* hNameMappings;
        public char* lpszProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationRaw(SHFILEOPSTRUCTW* op);

    private static nint AllocMultiString(IList<string> items)
    {
        int chars = 1;
        foreach (var s in items) chars += s.Length + 1;
        nint mem = Marshal.AllocHGlobal(chars * 2);
        unsafe
        {
            char* d = (char*)mem;
            foreach (var s in items)
            {
                foreach (char c in s) *d++ = c;
                *d++ = '\0';
            }
            *d = '\0';
        }
        return mem;
    }

    // Mueve/copia fromPaths a toDir. flags=0 + hwnd real = UI de progreso
    // del shell (deshacer incluido con ALLOWUNDO). Devuelve 0 = ok.
    public static unsafe int FileOperation(nint hwndParent, uint func, IList<string> fromPaths, string toDir, ushort flags)
    {
        if (fromPaths.Count == 0 || toDir.Length == 0) return -1;
        nint from = AllocMultiString(fromPaths);
        nint to = AllocMultiString(new[] { toDir });
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                hwnd = hwndParent,
                wFunc = func,
                pFrom = (char*)from,
                pTo = (char*)to,
                fFlags = flags,
            };
            return SHFileOperationRaw(&op);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
            Marshal.FreeHGlobal(to);
        }
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

    // =============== archivos virtuales (imagenes web: FileGroupDescriptorW + FileContents) ===============
    // Los navegadores no dan CF_HDROP: dan descriptor (nombres) + stream por
    // indice. Se parsea el descriptor (HGLOBAL) y cada contenido (ISTREAM,
    // fallback HGLOBAL) con lecturas por chunks (sin confiar en tamanos).
    public sealed record VirtualFile(string FileName, byte[] Content);

    private const int FileDescriptorSize = 592;   // sizeof(FILEDESCRIPTORW, layout shlobj.h)
    private const int FileNameOffset = 72;        // offset de cFileName[260]
    private const int MaxVirtualBytes = 100 * 1024 * 1024;

    public static List<VirtualFile>? ReadVirtualFiles(void* dataObjUnknown)
    {
        try
        {
            var unk = (Windows.Win32.System.Com.IUnknown*)dataObjUnknown;
            Guid iidData = Windows.Win32.System.Com.IDataObject.IID_Guid;
            void* pv = null;
            if (unk->QueryInterface(&iidData, &pv) != 0) return null;
            var data = (Windows.Win32.System.Com.IDataObject*)pv;
            try
            {
                uint cfDesc = RegisterClipboardFormat("FileGroupDescriptorW");
                if (cfDesc == 0) return null;
                if (!HasFormat(data, cfDesc)) return null;
                var names = ParseDescriptors(data, cfDesc);
                if (names.Count == 0) return null;
                uint cfContents = RegisterClipboardFormat("FileContents");
                if (cfContents == 0) return null;
                var results = new List<VirtualFile>(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    var bytes = ReadContents(data, cfContents, i);
                    if (bytes is not null && bytes.Length > 0)
                        results.Add(new VirtualFile(SanitizeFileName(names[i]), bytes));
                }
                return results.Count > 0 ? results : null;
            }
            finally { _ = data->Release(); }
        }
        catch { return null; }
    }

    private static bool HasFormat(Windows.Win32.System.Com.IDataObject* data, uint cf)
    {
        var fmt = new FORMATETC { cfFormat = (ushort)cf, dwAspect = 1, lindex = -1, tymed = (uint)TYMED.TYMED_HGLOBAL };
        try { data->QueryGetData(fmt); return true; }
        catch { return false; }
    }

    // parseo del descriptor sobre memoria ya lockeada (testeable con buffer fabricado)
    internal static List<string> ParseDescriptorNames(void* mem)
    {
        var names = new List<string>();
        uint count = *(uint*)mem;
        if (count == 0 || count > 64) return names;
        char* base_ = (char*)((byte*)mem + 4);
        for (uint i = 0; i < count; i++)
        {
            char* name = (char*)((byte*)base_ + i * FileDescriptorSize + FileNameOffset);
            int len = 0;
            while (len < 260 && name[len] != '\0') len++;
            names.Add(new string(name, 0, len));
        }
        return names;
    }

    private static List<string> ParseDescriptors(Windows.Win32.System.Com.IDataObject* data, uint cf)
    {
        var names = new List<string>();
        var fmt = new FORMATETC { cfFormat = (ushort)cf, dwAspect = 1, lindex = -1, tymed = (uint)TYMED.TYMED_HGLOBAL };
        var medium = default(STGMEDIUM);
        try { data->GetData(in fmt, out medium); }
        catch { return names; }
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.u.hGlobal.Value is null) return names;
            void* p = GlobalLock(medium.u.hGlobal);
            if (p is null) return names;
            try { return ParseDescriptorNames(p); }
            finally { _ = GlobalUnlock(medium.u.hGlobal); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    private static byte[]? ReadContents(Windows.Win32.System.Com.IDataObject* data, uint cf, int index)
    {
        // ISTREAM primero (navegadores), HGLOBAL despues. IStream por vtable
        // cruda slot 3 (sin depender del shape generado).
        foreach (uint ty in new[] { (uint)TYMED.TYMED_ISTREAM, (uint)TYMED.TYMED_HGLOBAL })
        {
            var fmt = new FORMATETC { cfFormat = (ushort)cf, dwAspect = 1, lindex = index, tymed = ty };
            try { data->QueryGetData(fmt); }
            catch { continue; }
            var medium = default(STGMEDIUM);
            try { data->GetData(in fmt, out medium); }
            catch { continue; }
            try
            {
                if (medium.tymed == TYMED.TYMED_ISTREAM && medium.u.pstm is not null)
                    return ReadAllFromStream(medium.u.pstm);
                if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.u.hGlobal.Value is not null)
                {
                    void* p = GlobalLock(medium.u.hGlobal);
                    if (p is null) return null;
                    try
                    {
                        nuint size = GlobalSize(medium.u.hGlobal);
                        if (size == 0 || size > (nuint)MaxVirtualBytes) return null;
                        var buf = new byte[size];
                        Marshal.Copy((nint)p, buf, 0, (int)size);
                        return buf;
                    }
                    finally { _ = GlobalUnlock(medium.u.hGlobal); }
                }
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        return null;
    }

    private static unsafe byte[]? ReadAllFromStream(void* pstm)
    {
        // chunks hasta EOF (sin confiar en Stat): origenes que mienten el size
        var out_ = new System.IO.MemoryStream();
        try
        {
            void** vt = *(void***)pstm;
            var read = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint*, int>)vt[3];
            byte[] chunk = new byte[65536];
            fixed (byte* cb = chunk)
            {
                uint got = 0;
                while (out_.Length < MaxVirtualBytes)
                {
                    int hr = read(pstm, cb, 65536, &got);
                    if (hr != 0 || got == 0) break;
                    out_.Write(chunk, 0, (int)got);
                    if (got < 65536) break;
                }
            }
            return out_.Length > 0 ? out_.ToArray() : null;
        }
        catch { return null; }
        finally { out_.Dispose(); }
    }

    internal static string SanitizeFileName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? $"imagen_{Guid.NewGuid():N}.png" : name.Trim();
    }

    // snapshot a temp: el stack guarda archivos REALES (re-dropeables).
    // Devuelve el path final o null.
    public static string? SnapshotVirtualFile(string fileName, byte[] content)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViennaBar", "drop");
            System.IO.Directory.CreateDirectory(dir);
            if (content.Length == 0 || content.Length > MaxVirtualBytes) return null;
            string dest = UniquePath(System.IO.Path.Combine(dir, SanitizeFileName(fileName)));
            System.IO.File.WriteAllBytes(dest, content);
            return dest;
        }
        catch { return null; }
    }

    internal static string UniquePath(string path)
    {
        if (!System.IO.File.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path)!;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string ext = System.IO.Path.GetExtension(path);
        int i = 1;
        string candidate;
        do { candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}"); i++; }
        while (System.IO.File.Exists(candidate));
        return candidate;
    }

    // =============== thumbnails: IShellItemImageFactory -> BGRA premultiplicado ===============
    // HBITMAP -> DIBits (GetDC de pantalla) -> buffer BGRA normalizado
    // (a==0 se asume padding opaco; si no, premultiplica). El core sube el
    // buffer a D2D una vez y lo cachea por path.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern void* GetDCRaw(void* hwnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDCRaw(void* hwnd, void* hdc);

    [DllImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static extern int GetDIBitsRaw(void* hdc, void* hbmp, uint start, uint lines,
        void* bits, BITMAPINFOHEADER* info, uint usage);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObjectRaw(void* obj);

    // Devuelve (buffer CoTaskMem BGRA, w, h). Liberar con FreeThumbnail.
    // null = sin thumb (el caller dibuja solo texto).
    public static (nint buf, int w, int h)? GetThumbnailPixels(string path, int size)
    {
        if (string.IsNullOrEmpty(path) || size < 16 || size > 256) return null;
        void* hbmp = null;
        try
        {
            Guid iid = Windows.Win32.UI.Shell.IShellItemImageFactory.IID_Guid;
            void* raw = null;
            // SHCreateItemFromParsingName(path, null, IID_IShellItemImageFactory, &raw)
            fixed (char* p = path)
            {
                var hr = SHCreateItemFromParsingName(p, null, &iid, &raw);
                if (hr.Failed || raw is null) return null;
            }
            var img = (Windows.Win32.UI.Shell.IShellItemImageFactory*)raw;
            try
            {
                var sz = new Windows.Win32.Foundation.SIZE { cx = size, cy = size };
                var hb = default(Windows.Win32.Graphics.Gdi.HBITMAP);
                try
                {
                    // GetImage(SIZE, SIIGBF_RESIZETOFIT=0, &HBITMAP): void, throw en fallo
                    img->GetImage(sz, 0, &hb);
                }
                catch { return null; }
                if (hb.Value == 0) return null;
                hbmp = (void*)hb.Value;
            }
            finally { _ = img->Release(); }
        }
        catch { return null; }
        try { return HBitmapToBgra(hbmp, size); }
        finally { DeleteObjectRaw(hbmp); }
    }

    private static unsafe (nint buf, int w, int h)? HBitmapToBgra(void* hbmp, int size)
    {
        void* hdc = GetDCRaw(null);
        if (hdc is null) return null;
        try
        {
            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size,   // top-down: sin flip
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,   // BI_RGB
            };
            int bytes = size * size * 4;
            nint mem = Marshal.AllocCoTaskMem(bytes);
            try
            {
                int lines = GetDIBitsRaw(hdc, hbmp, 0, (uint)size, (void*)mem, &bi, 0);
                if (lines == 0) { Marshal.FreeCoTaskMem(mem); return null; }
                // normalizar alfa: padding opaco (a==0 -> 255), resto premultiplicado
                uint* px = (uint*)mem;
                int n = size * size;
                for (int i = 0; i < n; i++)
                {
                    uint p = px[i];
                    uint a = p >> 24;
                    if (a == 0) px[i] = p | 0xFF000000u;
                    else if (a != 255)
                    {
                        uint r = ((p >> 16) & 255) * a / 255;
                        uint g = ((p >> 8) & 255) * a / 255;
                        uint b = (p & 255) * a / 255;
                        px[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
                return (mem, size, size);
            }
            catch { Marshal.FreeCoTaskMem(mem); return null; }
        }
        finally { _ = ReleaseDCRaw(null, hdc); }
    }

    public static void FreeThumbnail(nint buf)
    {
        if (buf != 0) Marshal.FreeCoTaskMem(buf);
    }

    // snapshot de archivo REAL al apilar: la barra guarda copia propia en
    // temp (inmune a MOVE/DELETE del original). Dirs, faltantes y archivos
    // sobre el tope quedan por referencia (comportamiento anterior).
    // El menu deferral (Mover/Copiar) opera con ORIGINALES (no pasa por aca).
    internal static string SnapshotRealFile(string path, long maxBytes = 256L * 1024 * 1024)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return path;
            if (new System.IO.FileInfo(path).Length > maxBytes) return path;
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViennaBar", "drop");
            System.IO.Directory.CreateDirectory(dir);
            string dest = UniquePath(System.IO.Path.Combine(dir,
                SanitizeFileName(System.IO.Path.GetFileName(path))));
            System.IO.File.Copy(path, dest);
            return dest;
        }
        catch { return path; }
    }

    // el stack vive en memoria: al arrancar, los snapshots huerfanos se borran
    internal static void ClearDropSnapshots()
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViennaBar", "drop");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }
        catch { }
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
