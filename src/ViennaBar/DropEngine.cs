using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Ole;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// DropEngine â€” IDropTarget via CCW manual del ViennaBar.Shell (AOT-total).
// El parseo CF_HDROP vive en ShellNative.PathsFromDataObject.
internal sealed class DropEngine : ViennaBar.ShellNative.DropTargetCcw, IDisposable
{
    private readonly ShellTree _tree;
    private HWND _hwnd;

    public readonly List<string> DropStack = new();

    public DropEngine(ShellTree tree) => _tree = tree;

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _ = RegisterDragDropRaw(_hwnd);
    }

    private unsafe bool RegisterDragDropRaw(HWND hwnd)
    {
        // RegisterDragDrop via P/Invoke crudo: toma IUnknown* del CCW
        [DllImport("ole32.dll", EntryPoint = "RegisterDragDrop")]
        static extern int RegisterDragDropRawImpl(void* hwnd, void* pDropTarget);

        return RegisterDragDropRawImpl((void*)hwnd.Value, (void*)IUnknownPtr) == 0;
    }

    public void Detach() => _ = RevokeDragDrop(_hwnd);

    protected override unsafe int OnDragEnter(void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        App.Instance?.SetDragActive(true);
        App.Instance?.Invalidate();
        if (effect is not null) *effect = 1; // DROPEFFECT_COPY
        return 0;
    }

    protected override unsafe int OnDragOver(uint keyState, PointL pt, uint* effect)
    {
        if (effect is not null) *effect = 1; // DROPEFFECT_COPY
        return 0;
    }

    protected override int OnDragLeave()
    {
        App.Instance?.SetDragActive(false);
        return 0;
    }

    protected override unsafe int OnDrop(void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        App.Instance?.SetDragActive(false);
        var paths = ViennaBar.ShellNative.ShellNative.PathsFromDataObject(dataObj);
        if (paths.Count == 0)
        {
            // sin CF_HDROP: probar archivos virtuales (imagenes web) con
            // snapshot a temp — el stack guarda archivos REALES re-dropeables
            var virtuals = ViennaBar.ShellNative.ShellNative.ReadVirtualFiles(dataObj);
            if (virtuals is not null)
            {
                foreach (var vf in virtuals)
                {
                    var snap = ViennaBar.ShellNative.ShellNative.SnapshotVirtualFile(vf.FileName, vf.Content);
                    if (snap is not null) paths.Add(snap);
                }
                if (paths.Count > 0)
                    App.AppLog($"[drop] +{paths.Count} virtual snapshot (total {DropStack.Count + paths.Count})");
            }
        }
        // COPY optimista: el deferral (menu Mover/Copiar/Apilar) ejecuta la
        // operacion real despues; si el usuario apila o cancela, el origen
        // conserva los archivos — consistente en todos los casos.
        if (effect is not null) *effect = 1;
        if (paths.Count > 0)
            App.Instance?.DeferDrop(paths, pt.X, pt.Y);
        return 0;
    }

    internal void StackPaths(List<string> paths)
    {
        foreach (var p in paths) DropStack.Add(p);
        if (paths.Count > 0)
        {
            App.AppLog($"[drop] +{paths.Count} al stack (total {DropStack.Count}): {paths[0]}");
            App.Instance?.Invalidate();
        }
    }

    // poda de seguridad: paths que ya no existen (movidos/borrados fuera de
    // nuestro control, o efecto COPY sobre original efimero). Red de
    // contencion para thumbs fantasmas. OJO: share de red caida tambien da
    // false (v1: aceptado, el re-drop lo restaura).
    internal void PruneMissing()
    {
        int before = DropStack.Count;
        DropStack.RemoveAll(p => !System.IO.File.Exists(p));
        if (DropStack.Count != before)
        {
            App.AppLog($"[drop] poda {before - DropStack.Count} muertos");
            App.Instance?.Invalidate();
        }
    }

    public new void Dispose()
    {
        Detach();
        base.Dispose();
    }
}
