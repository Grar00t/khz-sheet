using System;
using System.Runtime.InteropServices;
namespace KHZ.Sheet.Core
{
    /// <summary>Owns scratch formula IR independently of persistent sheet state.</summary>
    internal sealed unsafe class NativeScratchArena : SafeHandle
    {
        internal const nuint FormulaBytes = 1u << 20;
        private NativeScratchArena(IntPtr value) : base(IntPtr.Zero, true) { SetHandle(value); }
        public override bool IsInvalid => handle == IntPtr.Zero;

        [DllImport(KhzNative.Lib, EntryPoint = "khz_arena_init", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Init(void* arena, nuint capacity);
        [DllImport(KhzNative.Lib, EntryPoint = "khz_arena_destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Destroy(void* arena);

        internal static SheetStatus TryCreate(out NativeScratchArena? result)
        {
            result = null;
            SheetStatus gate = KhzAbi.Verify(out _);
            if (gate != SheetStatus.Ok) return gate;
            KhzAbiSizes sizes = default;
            int rc = KhzNative.AbiSizes(&sizes);
            if (rc != 0) return (SheetStatus)rc;
            if (sizes.ArenaBytes == 0 || sizes.ArenaBytes > 4096) return SheetStatus.ErrUnsupported;
            nuint bytes = ((nuint)sizes.ArenaBytes + 63u) & ~(nuint)63u;
            void* block = NativeMemory.AlignedAlloc(bytes, 64);
            if (block == null) return SheetStatus.ErrMemory;
            NativeMemory.Clear(block, bytes);
            if (Init(block, FormulaBytes) != 0)
            {
                NativeMemory.AlignedFree(block);
                return SheetStatus.ErrMemory;
            }
            try { result = new NativeScratchArena((IntPtr)block); }
            catch (OutOfMemoryException)
            {
                Destroy(block);
                NativeMemory.AlignedFree(block);
                return SheetStatus.ErrMemory;
            }
            return SheetStatus.Ok;
        }

        protected override bool ReleaseHandle()
        {
            Destroy((void*)handle);
            NativeMemory.AlignedFree((void*)handle);
            return true;
        }
    }
}
