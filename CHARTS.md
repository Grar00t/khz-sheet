# Chart support

Core `ChartDefinition` supports Bar, Column and Line charts with one category range and one numeric series. Both ranges are single columns with equal lengths (at most 4096 points); the desktop viewport and maximum of 32 charts further bound use. Select two adjacent columns of labels and values, choose a type and use **Add Chart**. **Charts** opens existing definitions.

`WorksheetSession.SampleChart` reads evaluated native cells. Missing, dirty or error values appear as gaps. `ChartView` renders positive/negative values and zero baselines using WPF. Open chart windows refresh when native values change, and offer manual refresh. Each view exposes a UI Automation name and an exact-fraction textual data description. This is a tested accessibility surface, not a screen-reader workflow certification.

The OPC presentation layer creates `xl/charts/chartN.xml`, `xl/drawings/drawing1.xml`, drawing/chart relationships, anchors and content types. Series formulas reference the current worksheet; cached values are sampled from the native evaluator. The importer accepts this single-series subset, verifies matching source ranges and refuses cross-sheet/external series. It recalculates formulas rather than trusting workbook caches.

Pixel coordinates and chart caches use floating-point presentation values. No such value is written back into the native rational engine. Native numbers remain canonical int64 fractions; XLSX numeric transport retains its documented rounding boundary.

Tests: `KhzSubsystemContracts` checks all three models, native value sampling, relationship round trips, rejection of unsupported sources, actual WPF rendering, and exact UI Automation text. Multiple series, editable chart placement, secondary axes, other chart types and general DrawingML preservation are not implemented. Native cell proofs do not attest chart metadata.
