using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace ViennaBar.Spike.oledd;

// constantes HRESULT de OLE drag/drop (no generadas por CsWin32)
internal static class OleHresult
{
    public const int DRAGDROP_S_DROP = unchecked((int)0x00040100);
    public const int DRAGDROP_S_CANCEL = unchecked((int)0x00040101);
    public const int DRAGDROP_S_USEDEFAULTCURSORS = unchecked((int)0x00040102);
}

// S3 — valida OLE drag/drop en ambas direcciones:
//  IN : ventana dropzone con IDropTarget managed (CCW) que parsea CF_HDROP.
//  OUT: clic izq inicia DoDragDrop con IDataObject del shell (drag al Explorer).
// Cerrar: ESC. El estado se refleja en el titulo de la ventana.
internal static unsafe class Program
{
    private const string WndClass = "ViennaBarSpike3";
    private static HWND _hwnd;
    private static DropTarget? _dropTarget;
    private static string _status = "S3: arrastra archivos aqui | clic izq = drag out";

    [STAThread]
    private static int Main()
    {
        _ = OleInitialize();
        var hinst = GetModuleHandle(default(PCWSTR));

        fixed (char* wc = WndClass)
        {
            var wcx = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (WNDPROC)WndProc,
                hInstance = (HINSTANCE)hinst.Value,
                hCursor = LoadCursor(default, IDC_ARROW),
                lpszClassName = wc,
            };
            _ = RegisterClassEx(in wcx);
        }

        var hmon = MonitorFromWindow(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        _ = GetMonitorInfo(hmon, ref mi);
        int w = 460, h = 260;
        int x = mi.rcMonitor.left + ((mi.rcMonitor.Width - w) / 2);
        int y = mi.rcMonitor.top + ((mi.rcMonitor.Height - h) / 3);

        fixed (char* wc = WndClass, title = "S3 OLE dnd")
        {
            _hwnd = CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOPMOST, wc, title,
                WINDOW_STYLE.WS_OVERLAPPEDWINDOW, x, y, w, h,
                default, default, hinst, null);
        }

        _dropTarget = new DropTarget();
        var hr = RegisterDragDrop(_hwnd, _dropTarget);
        _status = hr.Succeeded ? "DropTarget OK - arrastra archivos del Explorer" : $"RegisterDragDrop FAIL 0x{(int)hr:X}";

        _ = ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        UpdateText();

        while (GetMessage(out var msg, default, 0, 0))
        {
            TranslateMessage(in msg);
            _ = DispatchMessage(in msg);
        }
        _ = RevokeDragDrop(_hwnd);
        return 0;
    }

    private static void UpdateText()
    {
        SetWindowText(_hwnd, _status);
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                var ps = default(PAINTSTRUCT);
                var hdc = BeginPaint(hwnd, out ps);
                var brush = CreateSolidBrush(new COLORREF(0x00E8E5B2));
                _ = FillRect(hdc, &ps.rcPaint, brush);
                _ = DeleteObject((HGDIOBJ)brush.Value);
                _ = EndPaint(hwnd, in ps);
                return default;
            }

            case WM_LBUTTONDOWN:
                StartDragOut();
                return default;

            case WM_KEYDOWN:
                if ((int)wparam.Value == 0x1B) // VK_ESCAPE
                {
                    _ = DestroyWindow(hwnd);
                }
                return default;

            case WM_DESTROY:
                PostQuitMessage(0);
                return default;
        }
        return DefWindowProc(hwnd, msg, wparam, lparam);
    }

    // drag-out: IDataObject del shell para un archivo del Desktop + DoDragDrop
    private static void StartDragOut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var file = Directory.GetFiles(desktop).FirstOrDefault();
        if (file is null)
        {
            _status = "drag-out: sin archivos en Desktop";
            UpdateText();
            return;
        }

        HRESULT hr = SHGetDesktopFolder(out var desktopFolder);
        if (hr.Failed) return;

        ITEMIDLIST* pidl = null;
        uint attr = 0;
        fixed (char* p = file)
        {
            try { desktopFolder.ParseDisplayName(default, default, p, null, &pidl, ref attr); }
            catch { pidl = null; }
        }
        if (pidl is null) return;

        IDataObject? dataObj = null;
        Guid iidData = typeof(IDataObject).GUID;
        try
        {
            ITEMIDLIST* child = ILFindLastID(pidl);
            desktopFolder.GetUIObjectOf(default, 1, &child, &iidData, null, out var obj);
            dataObj = obj as IDataObject;
        }
        finally { ILFree(pidl); }

        if (dataObj is null)
        {
            _status = "drag-out: GetUIObjectOf sin IDataObject";
            UpdateText();
            return;
        }

        // CsWin32 DoDragDrop toma ComTypes.IDataObject — adapt via QI cast
        var ctDataObj = (System.Runtime.InteropServices.ComTypes.IDataObject)dataObj;

        DROPEFFECT effect = default;
        hr = DoDragDrop(ctDataObj, new DropSourceInterop(),
            DROPEFFECT.DROPEFFECT_COPY | DROPEFFECT.DROPEFFECT_MOVE | DROPEFFECT.DROPEFFECT_LINK,
            out effect);
        _status = $"DoDragDrop hr=0x{(int)hr:X} effect={effect}";
        UpdateText();
    }

    private static ITEMIDLIST* ILFindLastID(ITEMIDLIST* pidl)
    {
        ITEMIDLIST* cur = pidl;
        while (cur->mkid.cb != 0)
        {
            var next = (ITEMIDLIST*)((byte*)cur + cur->mkid.cb);
            if (next->mkid.cb == 0) break;
            cur = next;
        }
        return cur;
    }
}

// IDropTarget managed: el CCW de .NET genera la vtable COM automáticamente.
internal sealed unsafe class DropTarget : global::Windows.Win32.System.Ole.IDropTarget
{
    public void DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
        Console.WriteLine("[DropTarget] DragEnter");
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }

    public void DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }

    public void DragLeave()
    {
        Console.WriteLine("[DropTarget] DragLeave");
    }

    public void Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
        Console.WriteLine("[DropTarget] Drop!");
        try
        {
            var fmt = new FORMATETC
            {
                cfFormat = 15, // CF_HDROP
                dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = (uint)TYMED.TYMED_HGLOBAL,
            };
            pDataObj.GetData(&fmt, out var medium);
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.u.hGlobal.Value != null)
            {
                var hDrop = new global::Windows.Win32.UI.Shell.HDROP((nint)medium.u.hGlobal.Value);
                uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                Console.WriteLine($"  CF_HDROP: {n} archivos");
                for (uint i = 0; i < n && i < 10; i++)
                {
                    Span<char> buf = stackalloc char[260];
                    fixed (char* p = buf)
                    {
                        _ = DragQueryFile(hDrop, i, p, 260);
                        Console.WriteLine($"    [{i}] {new string(p)}");
                    }
                }
            }
            ReleaseStgMedium(ref medium);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  GetData CF_HDROP error: {ex.Message}");
        }
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }
}

// IDropSource mínimo para DoDragDrop
internal sealed unsafe class DropSourceInterop : global::Windows.Win32.System.Ole.IDropSource
{
    public HRESULT QueryContinueDrag(BOOL fEscapePressed, MODIFIERKEYS_FLAGS grfKeyState)
    {
        if (fEscapePressed) return (HRESULT)OleHresult.DRAGDROP_S_CANCEL;
        // boton izq (MK_LBUTTON) soltado → drop
        if ((grfKeyState & MODIFIERKEYS_FLAGS.MK_LBUTTON) == 0)
            return (HRESULT)OleHresult.DRAGDROP_S_DROP;
        return default;
    }

    public HRESULT GiveFeedback(DROPEFFECT dwEffect) => (HRESULT)OleHresult.DRAGDROP_S_USEDEFAULTCURSORS;
}
