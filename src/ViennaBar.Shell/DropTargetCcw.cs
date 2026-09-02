using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;
using static Windows.Win32.PInvoke;

namespace ViennaBar.ShellNative;

// CCW manual de IDropTarget para RegisterDragDrop (AOT-total).
internal unsafe class DropTargetCcw : IDisposable
{
    // ---- shape del objeto COM en memoria: [vtable*][this-slot][refcount] ----
    private struct ComObject
    {
        public void** Vtbl;
        public void* Managed;   // GCHandle.ToIntPtr del DropTargetCcw
        public int RefCount;
    }

    // POINTL crudo para los callbacks (posiciÃ³n del cursor)
    public readonly struct PointL
    {
        public readonly int X, Y;
        public PointL(int x, int y) { X = x; Y = y; }
    }

    private static readonly void** s_vtbl = BuildVtable();

    private static void** BuildVtable()
    {
        var vt = (void**)Marshal.AllocHGlobal(sizeof(void*) * 7);
        vt[0] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, void**, int>)&QueryInterfaceImpl;
        vt[1] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&AddRefImpl;
        vt[2] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&ReleaseImpl;
        vt[3] = (delegate* unmanaged[Stdcall]<ComObject*, void*, uint, PointL, uint*, int>)&DragEnterImpl;
        vt[4] = (delegate* unmanaged[Stdcall]<ComObject*, uint, PointL, uint*, int>)&DragOverImpl;
        vt[5] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&DragLeaveImpl;
        vt[6] = (delegate* unmanaged[Stdcall]<ComObject*, void*, uint, PointL, uint*, int>)&DropImpl;
        return vt;
    }

    private ComObject* _com;
    private GCHandle _self;

    public nint IUnknownPtr => (nint)_com;

    public DropTargetCcw()
    {
        _com = (ComObject*)Marshal.AllocHGlobal(sizeof(ComObject));
        _com->Vtbl = s_vtbl;
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _com->Managed = (void*)GCHandle.ToIntPtr(_self);
        _com->RefCount = 1;
    }

    // ---- callbacks COM ----
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceImpl(ComObject* self, Guid* riid, void** ppv)
    {
        Guid iidDropTarget = new(0x00000122, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        Guid iidUnknown = new(0x00000000, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        if (*riid == iidDropTarget || *riid == iidUnknown)
        {
            *ppv = self;
            self->RefCount++;
            return 0;
        }
        *ppv = null;
        return -2147467263; // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int AddRefImpl(ComObject* self) => ++self->RefCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ReleaseImpl(ComObject* self) => --self->RefCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DragEnterImpl(ComObject* self, void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        return FromCom(self).OnDragEnter(dataObj, keyState, pt, effect);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DragOverImpl(ComObject* self, uint keyState, PointL pt, uint* effect)
    {
        return FromCom(self).OnDragOver(keyState, pt, effect);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DragLeaveImpl(ComObject* self)
    {
        return FromCom(self).OnDragLeave();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DropImpl(ComObject* self, void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        return FromCom(self).OnDrop(dataObj, keyState, pt, effect);
    }

    private static DropTargetCcw FromCom(ComObject* self) =>
        (DropTargetCcw)GCHandle.FromIntPtr((nint)self->Managed).Target!;

    // ---- lÃ³gica managed (override por el core) ----
    protected virtual int OnDragEnter(void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        if (effect is not null) *effect = 1; // DROPEFFECT_COPY
        return 0;
    }

    protected virtual int OnDragOver(uint keyState, PointL pt, uint* effect)
    {
        if (effect is not null) *effect = 1;
        return 0;
    }

    protected virtual int OnDragLeave() => 0;

    protected virtual int OnDrop(void* dataObj, uint keyState, PointL pt, uint* effect)
    {
        if (effect is not null) *effect = 1;
        return 0;
    }

    public void Dispose()
    {
        if (_com is not null && _com->RefCount > 0) _com->RefCount--;
        if (_com is not null && _com->RefCount <= 0)
        {
            _self.Free();
            Marshal.FreeHGlobal((nint)_com);
            _com = null;
        }
    }
}
