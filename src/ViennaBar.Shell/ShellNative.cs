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
        catch { return null; }
    }

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

    // ---------- menÃº contextual interactivo ----------
    public static void ShowContextMenu(IShellFolder* parentFolder, byte[] childPidl, HWND hwnd, int screenX, int screenY)
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
                    menu->QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                    int cmd = TrackPopupMenu(hmenu,
                        TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
                        screenX, screenY, 0, hwnd, null);
                    if (cmd <= 0) return;
                    var ici = new CMINVOKECOMMANDINFO
                    {
                        cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
                        lpVerb = (PCSTR)(byte*)(nint)(cmd - 1), // MAKEINTRESOURCEA
                        nShow = 5,
                    };
                    menu->InvokeCommand(in ici);
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

        if (TryCreateItemFromParsingName(parsingName, out var item).Failed) return 0;
        try
        {
            return TryBindItemToFolder(item, out var sf) ? (nint)sf : 0;
        }
        finally { _ = item->Release(); }
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

    public static void ReleaseFolder(nint folderHandle)
    {
        if (folderHandle != 0) _ = ((IShellFolder*)folderHandle)->Release();
    }

    public static nint OpenAppsFolder() => OpenFolderByParsingName(
        "::{" + AppsFolderId.ToString("D") + "}");

    // =============== drop: parse CF_HDROP de un IDataObject* crudo ===============
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
