# Desktop boundary

The WPF application targets .NET 8 on Windows. `WorksheetSession` maintains a bounded 256-by-52 DataTable view while C11 owns numerical values, evaluation, dependency edges and cell proofs. No third-party UI, chart or Open XML packages are introduced.

If the native engine cannot load, the worksheet displays its captured load error and disables calculation-dependent commands. Formula, numeric, boolean and error-value commits are rejected; plain text can still be entered. The UI does not treat text-only display as a working calculation engine.

Open XLSX imports the supported single-worksheet subset into a separate session. Styles, dimensions, frozen columns, preset theme, semantic tables and chart definitions are supported. Unknown parts or unsupported constructs are refused. Workbook tabs can be renamed, deleted (with confirmation), duplicated and reordered, but XLSX export contains only the active sheet and warns before writing.

Formatting includes font family/size, bold/italic/underline, RGB text/fill colors, alignment, wrapping, independent borders and built-in number formats. Explicit styles serialize to `styles.xml` and worksheet style indexes; CSV remains value-only. Spreadsheet error values receive a distinct cell style. The selected-cell status summary is presentation-only and uses floating-point display arithmetic; stored workbook values remain exact rationals.

Cell styling is applied when virtualized cells/rows are realized and when existing cells are refreshed. Header selection styling uses style triggers and a throttled update rather than allocating a row style for each selection event. Column widths are stored only after a resize drag completes or a column-header auto-fit; row heights are stored through the Row Height control. The fixed pixel-to-character conversion is not claimed to match Excel font metrics.

The minimal keyboard set includes type-to-edit through WPF, F2, Enter/Tab/Shift+Tab navigation, arrow navigation, Home/End/Ctrl+Arrow, Ctrl+D/Ctrl+R, rectangular text clipboard transfer, and Name Box navigation. Insert/delete row and column rebuild the native worksheet and adjust unqualified formula references; a removed reference evaluates as `#REF!`. Structural edits clear undo history. Column insert/delete inside semantic tables is refused; full Excel reference semantics are not claimed. Formula fill repeats source text and does not rewrite relative references.

Unsaved changes prompt on close and before New/Open actions. A debounced and periodic recovery snapshot is written through a temporary generation and atomically published manifest; startup offers recovery. Native-enabled sessions use per-sheet XLSX recovery files. If the engine is unavailable, recovery falls back to CSV and contains only permitted text inputs. Recovery is not a substitute for a named workbook save.

`KhzSubsystemContracts` contains interaction assertions for the engine-failure banner, recycled-cell formatting, dimension persistence, sheet operations, structural edits, Name Box/status behavior, and recovery. Run `pwsh scripts/verify-windows.ps1` on Windows for the WPF interactions and executable smoke check. A cross-target build on another OS checks compilation only; it does not prove Windows rendering or input behavior. `dotnet test` alone is not evidence because these managed projects are console assertion runners.

In the current Ubuntu environment, `verify-windows.ps1` passed configure, native build, and 18 native CTests, then stopped at `desktop-build` with `NETSDK1100`. A separate cross-target `dotnet build` compiled the desktop and contract projects with zero warnings, but the WPF runner and desktop smoke check were not executed.

Real-user usability, complete Excel compatibility, multi-sheet XLSX, complete localization/RTL, and exhaustive accessibility remain unverified.
