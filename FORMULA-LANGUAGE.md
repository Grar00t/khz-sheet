# Formula language

The native parser accepts optional leading `=`, invariant decimal/scientific literals, percentages, parentheses, A1 references with optional `$`, rectangular ranges, unary `+ -`, arithmetic `+ - * / ^`, and numeric comparisons `= <> < <= > >=`. Comparison binds below addition. Function names are ASCII case-insensitive; commas separate arguments. TRUE/FALSE constants lower to exact 1/0.

| Function | Native contract |
|---|---|
| SUM, AVG/AVERAGE, MIN, MAX | Existing exact aggregate semantics |
| COUNT, COUNTA | COUNT counts numeric range/reference cells; COUNTA counts nonempty cells, including Boolean and error cells; stored empty text counts as empty |
| PRODUCT | Multiplies numeric aggregate arguments; range text/Boolean cells are ignored; referenced errors propagate; an empty numeric set returns zero |
| ABS | Exact rational magnitude |
| ROUND(value, decimals) | Integer decimals in -18..18; nearest with ties away from zero; wide integer intermediates and reduced rational result |
| CEILING(value), FLOOR(value) | Exact integer toward positive/negative infinity; one argument only |
| IF(condition, true_value, false_value) | Numeric condition, zero is false; only the selected value arm is evaluated |

These are explicit KHZ policies, not complete Excel function compatibility. IF dependencies conservatively include both arms, so a cycle is rejected even if one arm would not execute. String-valued IF, significance arguments to CEILING/FLOOR, sheet-qualified references, structured references, lookup/text/date/array families remain unsupported. Intermediate aggregate overflow can be rejected even when rearranging the expression would fit.

`^` requires an integer exponent of magnitude at most 1024. Noninteger exponents fail closed. 0^0 is 1; zero to a negative exponent is division by zero.

Native limits: 8192 source bytes, 4096 IR nodes, 64 arguments and 64 parser/walk depth. Managed lexing is bounded to 8192 UTF-16 characters/tokens and managed recursion/lowering to 64 frames. Managed parsing preserves exact literal text; lowering refuses legacy `NumberNode(double)` values without `RawText`. It lowers colon range ASTs and the new operators/functions to the same native IR. The lexer/parser can still represent additional syntax which native lowering rejects.

`khz_formula_set` is transactional for parse, dependency declaration and cell installation. Low-level imported unsupported formula source may become a cell error on recalc; the strict desktop XLSX importer rejects unsupported formula installation and stages the whole workbook before replacing a session. Diagnostics remain English expectation text and byte offsets.

Tests: `khz_formula_extended_test`, `khz_formula_boundary_test`, `khz_rational_oracle.py`, `KhzExactLiteral`, `KhzFormulaCells`, `KhzSubsystemContracts`, dependency transaction/property tests. The managed/native differential corpus covers the requested functions and comparisons; it is not an exhaustive grammar-equivalence proof.
