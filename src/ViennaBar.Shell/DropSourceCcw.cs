using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace ViennaBar.ShellNative;

// CCW manual de IDropSource para DoDragDrop (AOT-total), mismo patrón que
// DropTargetCcw. El data object lo crea el shell (SHCreateDataObject), así
// que este CCW solo decide: ESC = cancel, botón izq soltado = drop.
internal unsafe class DropSourceCcw : IDisposable
{
    private struct ComObject
    {
        public void** Vtbl;
        public void* Managed;
        public int RefCount;
    }

    private static readonly void** s_vtbl = BuildVtable();

    private static void** BuildVtable()
    {
        var vt = (void**)Marshal.AllocHGlobal(sizeof(void*) * 6);
        vt[0] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, void**, int>)&QueryInterfaceImpl;
        vt[1] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&AddRefImpl;
        vt[2] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&ReleaseImpl;
        vt[3] = (delegate* unmanaged[Stdcall]<ComObject*, int, uint, int>)&QueryContinueDragImpl;
        vt[4] = (delegate* unmanaged[Stdcall]<ComObject*, uint, int>)&GiveFeedbackImpl;
        return vt;
    }

    private ComObject* _com;
    private GCHandle _self;

    public nint IUnknownPtr => (nint)_com;

    public DropSourceCcw()
    {
        _com = (ComObject*)Marshal.AllocHGlobal(sizeof(ComObject));
        _com->Vtbl = s_vtbl;
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _com->Managed = (void*)GCHandle.ToIntPtr(_self);
        _com->RefCount = 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceImpl(ComObject* self, Guid* riid, void** ppv)
    {
        Guid iidDropSource = new(0x00000121, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        Guid iidUnknown = new(0x00000000, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
        if (*riid == iidDropSource || *riid == iidUnknown)
        {
            *ppv = self;
            self->RefCount++;
            return 0;
        }
        *ppv = null;
        return -2147467262; // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int AddRefImpl(ComObject* self) => ++self->RefCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ReleaseImpl(ComObject* self) => --self->RefCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryContinueDragImpl(ComObject* self, int escapePressed, uint keyState)
    {
        // DRAGDROP_S_CANCEL / DRAGDROP_S_DROP / S_OK
        if (escapePressed != 0) return unchecked((int)0x00040101);
        if ((keyState & 0x0001) == 0) return unchecked((int)0x00040100); // MK_LBUTTON
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GiveFeedbackImpl(ComObject* self, uint effect)
        => unchecked((int)0x00040102); // DRAGDROP_S_USEDEFAULTCURSORS

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
