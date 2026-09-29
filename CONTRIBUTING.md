# Contributing
Use an isolated branch and record the starting commit. Reproduce a defect with synthetic input before modifying its root cause. Keep public layout changes explicit and versioned. Do not add a runtime service or language dependency for test convenience.
Run relevant native/managed tests, then the supported clean matrix in BUILDING.md and TESTING.md. A new test must exercise behavior and failure publication, not merely assert that an executable starts. Keep known failures visible and separate from passing-suite totals.
Do not commit native binaries, local absolute paths, temporary ZIP/XLSX/SQLite files, private data or tool caches. Test-only Python is permitted for independent oracles and fixtures. Update the relevant contract and CHANGELOG.md with compatibility changes.
