using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KHZ.Sheet.Core;
using KHZ.Sheet.Desktop;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string label)
    {
        Interlocked.Increment(ref _checks);
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }
    private static NativeSheetSession Create()
    {
        Check(NativeSheetSession.TryCreate(8 * 1024 * 1024, 64, out var session) == SheetStatus.Ok && session is not null,
            "native session allocation and ABI gate");
        return session!;
    }
    private static void Ok(SheetStatus status, string label) => Check(status == SheetStatus.Ok, label + ": " + status);
    private static void NativeContracts()
    {
        using (NativeSheetSession sheet = Create())
        {
            Ok(sheet.SetInt64(0, 0, 10), "A1 literal");
            Ok(sheet.TrySetFormula(1, 0, "=A1*2", out _), "B1 formula");
            Ok(sheet.TrySetFormula(2, 0, "=B1+1", out _), "C1 formula");
            Ok(sheet.TryRecalculate(out ulong evaluated), "cascade recalc");
            Check(evaluated == 2, "two topological evaluations");
            Ok(sheet.TryGetCell(2, 0, out var cell), "C1 snapshot");
            Check(cell.Value.Num == 21 && cell.Value.Den == 1, "native C1=21");
            Check(cell.Text == IntPtr.Zero && cell.Formula == IntPtr.Zero, "no borrowed pointers escape");
            Ok(sheet.TryProofHex(out string before), "first proof");
            Ok(sheet.SetInt64(0, 0, 7), "update A1");
            Ok(sheet.TryRecalculate(out evaluated), "dependent recalc");
            Check(evaluated == 2, "both dependents evaluated");
            Ok(sheet.TryGetCell(2, 0, out cell), "updated C1");
            Check(cell.Value.Num == 15 && cell.Value.Den == 1, "native C1=15");
            Ok(sheet.TryProofHex(out string after), "second proof");
            Check(before != after && after.Length == 64, "SHA-256 chain changed");
            Ok(sheet.VerifyChain(out _), "chain verification");
            Ok(sheet.TryAudit(out var audit), "bounded chain audit");
            Check(audit.EntriesExamined == sheet.Commits && audit.CellsVerified == 3 && audit.Superseded > 0 && audit.Dropped == 0,
                "retained and superseded counts");
            Console.WriteLine("CASCADE_PROOF=" + after);
            ulong commits = sheet.Commits;
            Check(sheet.TrySetFormula(1, 0, "=1+", out _) != SheetStatus.Ok, "malformed formula rejected");
            Check(sheet.Commits == commits, "parse failure has no commit");
            Ok(sheet.SetRational(3, 0, -2, -4), "native rational normalization");
            Ok(sheet.TryGetCell(3, 0, out cell), "rational snapshot");
            Check(cell.Value.Num == 1 && cell.Value.Den == 2, "canonical 1/2");
            commits = sheet.Commits;
            Check(sheet.SetInt64(3, 0, long.MinValue) == SheetStatus.ErrOverflow, "INT64_MIN contract");
            Check(sheet.SetRational(3, 0, 1, 0) == SheetStatus.ErrDivZero, "zero denominator rejected");
            Check(sheet.Commits == commits, "rejected arithmetic leaves chain unchanged");
            Check(sheet.SetText(4, 0, "\ud800") == SheetStatus.ErrFormat, "invalid UTF-16 rejected");
            Check(sheet.SetText(4, 0, new byte[] { 0xff }) == SheetStatus.ErrFormat, "invalid UTF-8 rejected");
            Ok(sheet.SetText(4, 0, ""), "empty text");
            Ok(sheet.TryGetTextBytes(4, 0, out var empty), "read empty text");
            Check(empty.IsEmpty, "empty text round trip");
        }
        NativeSheetSession owner = Create();
        string text = "\u0627\u0644\u0633\u0644\u0627\u0645\0A";
        Ok(owner.SetText(0, 0, text), "Arabic and embedded NUL");
        Ok(owner.TryGetTextBytes(0, 0, out ReadOnlySpan<byte> copy), "owned text copy");
        owner.Dispose(); owner.Dispose();
        Check(Encoding.UTF8.GetString(copy) == text, "text survives native disposal");
        Check(owner.SetInt64(0, 0, 1) == SheetStatus.ErrState, "use after dispose refused");
        using (var cyclic = Create())
        {
            Ok(cyclic.TrySetFormula(0, 0, "=A1+1", out _), "store circular source");
            ulong before = cyclic.Commits;
            Check(cyclic.TryRecalculate(out ulong evaluated) == SheetStatus.ErrCycle && evaluated == 0,
                "cycle rejected before evaluation");
            Check(cyclic.Commits == before, "cycle rejection adds no result commit");
        }
        NativeSheetSession raced = Create();
        Task work = Task.Run(() => Parallel.For(0, 4000, i =>
        {
            SheetStatus status = raced.SetInt64(0, 0, i);
            Check(status is SheetStatus.Ok or SheetStatus.ErrState, "serialized native edit/dispose");
        }));
        Thread.Sleep(5);
        raced.Dispose();
        work.GetAwaiter().GetResult();
        WeakReference abandoned = AbandonSession();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!abandoned.IsAlive, "abandoned session collected");
        Console.WriteLine("NATIVE_SESSION_CONTRACTS=PASS");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonSession()
    {
        var session = Create();
        Ok(session.SetText(0, 0, "owned by SafeHandle"), "finalizer ownership setup");
        return new WeakReference(session);
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void WpfContracts()
    {
        App app = new();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow window = new();
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            var grid = (DataGrid)window.FindName("SheetGrid");
            var formula = (TextBox)window.FindName("FormulaBox");
            var sheets = (Selector)window.FindName("SheetList");
            var sheet = (WorksheetSession)sheets.SelectedItem;
            Check(sheet.EngineAvailable && !grid.IsReadOnly && formula.IsEnabled, "WPF native editing enabled");
            Button FindButton(string label) => Children<Button>(window).Single(b => b.Content?.ToString() == label);
            void Apply(int col, string source)
            {
                grid.CurrentCell = new DataGridCellInfo(sheet.Grid.DefaultView[0], grid.Columns[col]);
                formula.Text = source;
                FindButton("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
            }
            Apply(0, "10"); Apply(1, "=A1*2"); Apply(2, "=B1+1");
            Check(sheet.Grid.Rows[0][2].ToString() == "21", "Apply button invokes native formula path");
            ulong before = sheet.NativeCommits;
            grid.CurrentCell = new DataGridCellInfo(sheet.Grid.DefaultView[0], grid.Columns[0]);
            grid.SelectedCells.Clear(); grid.SelectedCells.Add(grid.CurrentCell);
            grid.ScrollIntoView(sheet.Grid.DefaultView[0], grid.Columns[0]); grid.UpdateLayout();
            Check(grid.BeginEdit(), "begin real DataGrid edit");
            Pump();
            TextBox? editor = grid.Columns[0].GetCellContent(sheet.Grid.DefaultView[0]) as TextBox;
            Check(editor is not null, "DataGrid generated editing textbox");
            editor!.Text = "7";
            Check(grid.CommitEdit(DataGridEditingUnit.Cell, true), "commit DataGrid cell");
            grid.CommitEdit(DataGridEditingUnit.Row, true); Pump();
            Check(sheet.Grid.Rows[0][2].ToString() == "15", "CellEditEnding recomputes native dependents");
            Check(sheet.NativeCommits == before + 3, "edit and two results entered proof chain");
            FindButton("Recalculate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            FindButton("Verify Proof").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            string status = ((TextBlock)window.FindName("StatusText")).Text;
            Check(status.Contains("retained proof verified", StringComparison.Ordinal), "Verify button reports bounded native audit");
            Check(sheet.TryGetProof(out string proof, out _), "WPF proof retrieval");
            Console.WriteLine("WPF_PROOF=" + proof);
            Console.WriteLine("WPF_ROUTED_BUTTONS_AND_DATAGRID=PASS");
        }
        finally { window.Close(); app.Shutdown(); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "no-native" or "missing-native")
            {
                using WorksheetSession sheet = new("MissingNative");
                Check(!sheet.EngineAvailable && sheet.EngineStatus == SheetStatus.ErrMissing, "native DLL genuinely unavailable");
                Check(!sheet.CommitCell(0, 0, "42", out _), "missing native edit rejected");
                Check(sheet.GetInput(0, 0) == "" && sheet.NativeCommits == 0, "missing native creates no fictitious state");
                Console.WriteLine("MISSING_NATIVE_FAIL_CLOSED=PASS");
            }
            else { NativeContracts(); WpfContracts(); }
            Console.WriteLine($"checks={_checks} failures=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
