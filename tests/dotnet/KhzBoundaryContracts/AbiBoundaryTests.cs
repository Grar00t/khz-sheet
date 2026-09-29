using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KHZ.Sheet.Core;
internal static class AbiBoundaryTests
{
    internal static int? Run(string[] args)
    {
        if (args.Length == 0) return null;
        if (args.Length == 2 && args[0] == "race") return Race(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "bad-image") return BadImage();
        return 2;
    }
    private static int Race(string library)
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeSheet).Assembly, (_, _, _) =>
        {
            Thread.Sleep(100); // Force overlap during the first verification, not after publication.
            return NativeLibrary.Load(library);
        });
        using Barrier barrier = new Barrier(9);
        SheetStatus[] results = new SheetStatus[8];
        Thread[] workers = new Thread[8];
        for (int i = 0; i < workers.Length; ++i)
        {
            int index = i;
            results[i] = SheetStatus.ErrState;
            workers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try { results[index] = KhzAbi.Verify(out _); }
                catch (Exception ex) { Console.WriteLine(ex.GetType().Name); }
            }) { IsBackground = true };
            workers[i].Start();
        }
        barrier.SignalAndWait();
        int failures = 0;
        for (int i = 0; i < workers.Length; ++i)
        {
            if (!workers[i].Join(10000)) return 2;
            Console.WriteLine("thread=" + i + " status=" + results[i]);
            if (results[i] != SheetStatus.Ok) ++failures;
        }
        Console.WriteLine("failures=" + failures);
        return failures == 0 ? 0 : 1;
    }
    private static int BadImage()
    {
        string file = Path.Combine(Path.GetTempPath(), "khz-invalid-image-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(file, new byte[256]);
            NativeLibrary.SetDllImportResolver(typeof(NativeSheet).Assembly, (_, _, _) => NativeLibrary.Load(file));
            SheetStatus status = KhzAbi.Verify(out string detail);
            Console.WriteLine("status=" + status + " detail=" + detail);
            return status == SheetStatus.ErrUnsupported || status == SheetStatus.ErrMissing ? 0 : 1;
        }
        finally { File.Delete(file); }
    }
}
