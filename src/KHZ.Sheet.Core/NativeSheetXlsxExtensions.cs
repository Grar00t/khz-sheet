namespace KHZ.Sheet.Core
{
    public static unsafe class NativeSheetXlsxExtensions
    {
        public static SheetStatus TryWriteXlsx(
            this NativeSheet sheet,
            string path,
            string? sheetName,
            out XlsxReport report)
        {
            report = default;

            if (sheet is null)
            {
                return SheetStatus.ErrNull;
            }

            if (sheet.Handle == null)
            {
                return SheetStatus.ErrState;
            }

            if (!KhzSheetLayout.XlsxAvailable)
            {
                return SheetStatus.ErrUnsupported;
            }

            return KhzXlsx.Write(sheet.Handle, path, sheetName, out report);
        }
    }
}
