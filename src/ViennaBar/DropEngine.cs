using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using static Windows.Win32.PInvoke;
using POINTL = Windows.Win32.Foundation.POINTL;

namespace ViennaBar;

// DropEngine — IDropTarget de la sidebar con IDropTargetHelper (imagen del OS)
// + registro de drops en el drop stack (F1: log; el stack visual va en F2).
internal sealed unsafe class DropEngine : global::Windows.Win32.System.Ole.IDropTarget, IDisposable
{
    private readonly ShellTree _tree;
    private HWND _hwnd;
    private global::Windows.Win32.UI.Shell.IDropTargetHelper? _helper;
    private HWND Target => _hwnd;

    public DropEngine(ShellTree tree) => _tree = tree;

    public readonly List<string> DropStack = new();

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        Guid iidHelper = typeof(global::Windows.Win32.UI.Shell.IDropTargetHelper).GUID;
        _ = CoCreateInstance(in CLSID_DragDropHelper, null, CLSCTX.CLSCTX_INPROC_SERVER, in iidHelper, out var obj);
        _helper = obj as global::Windows.Win32.UI.Shell.IDropTargetHelper;
        _ = RegisterDragDrop(_hwnd, this);
    }

    public void Detach() => _ = RevokeDragDrop(_hwnd);

    void global::Windows.Win32.System.Ole.IDropTarget.DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
        var p = new System.Drawing.Point(pt.x, pt.y);
        _helper?.DragEnter(_hwnd, pDataObj, &p, DROPEFFECT.DROPEFFECT_COPY);
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }

    void global::Windows.Win32.System.Ole.IDropTarget.DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
        var p = new System.Drawing.Point(pt.x, pt.y);
        _helper?.DragOver(&p, DROPEFFECT.DROPEFFECT_COPY);
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }

    void global::Windows.Win32.System.Ole.IDropTarget.DragLeave() => _helper?.DragLeave();

    void global::Windows.Win32.System.Ole.IDropTarget.Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
    {
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
                var hDrop = new Windows.Win32.UI.Shell.HDROP((nint)medium.u.hGlobal.Value);
                uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    Span<char> buf = stackalloc char[260];
                    fixed (char* p = buf)
                    {
                        _ = DragQueryFile(hDrop, i, p, 260);
                        DropStack.Add(new string(p));
                    }
                }
                Console.WriteLine($"[drop] +{n} al stack (total {DropStack.Count})");
            }
            ReleaseStgMedium(ref medium);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[drop] error: {ex.Message}");
        }
        var pd = new System.Drawing.Point(pt.x, pt.y);
        _helper?.Drop(pDataObj, &pd, DROPEFFECT.DROPEFFECT_COPY);
        if (pdwEffect is not null) *pdwEffect = DROPEFFECT.DROPEFFECT_COPY;
    }

    public void Dispose()
    {
        Detach();
        if (_helper is not null) Marshal.ReleaseComObject(_helper);
    }
}
