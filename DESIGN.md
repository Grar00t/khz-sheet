# KHZ Sheet design contract

## Product
KHZ Sheet is a local-first spreadsheet workstation backed by a deterministic native engine. The desktop UI exists to expose exact cell editing, proofs, import/export, and familiar spreadsheet controls without hiding engine boundaries.

## Visual direction
- Dense instrument-like spreadsheet, not a marketing dashboard.
- Dark application chrome; the worksheet grid remains the dominant surface.
- In application chrome, green is reserved for product identity/positive verification and blue is the interaction/selection accent. User-selected spreadsheet cell colors are independent document presentation.
- Decorative elements must encode state, identity, or navigation; no ambient gradients, cards, or ornamental badges.
- The logo/grid mark is the primary signature. Proof/status receipts are the secondary signature.

## Durable tokens
- Window: `#0D1117`
- Panel: `#161B22`
- Raised panel: `#1F2630`
- Grid/border: `#30363D`
- Text: `#E6EDF3`
- Muted text: `#8B949E`
- Identity/verified: `#2EA043` / hover `#3FB950`
- Selection/action: `#1F6FEB`

## Typography
- Application UI: Segoe UI, system fallback.
- Spreadsheet cell font is user-selectable and must not be overwritten by shell typography.
- Data/status receipts may use a monospaced face only when presenting hashes, proofs, coordinates, or machine output.

## Interaction contract
- Spreadsheet coordinates are authoritative; visual reordering must never silently change native `(row, column)` identity.
- Formatting is reversible through the same Undo/Redo history system as content edits.
- Selection must remain visually obvious over custom cell fills.
- Virtualized/recycled cells must reapply state from `WorksheetSession`; WPF containers are not the source of truth.
- Export must not claim persistence for a style unless that style is serialized and regression-tested.

## Export contract
- XLSX output uses the desktop cell default (`Segoe UI`, 11 pt) as the workbook base so implicit bold/underline styles do not silently switch font families.
- Existing native cell values/formulas remain the data source; presentation serialization must not mutate engine values or proof state.
- A styled export is committed to the requested path only after the native workbook and presentation patch both succeed.
- Unknown/unsupported workbook features are not synthesized or silently claimed.

## Accessibility floor
- Keyboard focus and selection remain visible.
- Formatting controls have textual labels or tooltips; color is never the sole state cue.
- User-entered text and formulas remain editable regardless of applied formatting.
