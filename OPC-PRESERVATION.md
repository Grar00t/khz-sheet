# OPC preservation
Managed `OpcPackage.cs` is separate from native XLSX reconstruction. Existing `KhzOpcPackage` tests exercise package editing and preservation. Untouched part payloads and ZIP-container bytes are different contracts: repackaging is not byte-identical ZIP preservation.
Use payload hashes to assess untouched parts. A successful managed OPC regression does not establish complete chart/table import, editing or rendering. Relationship graphs, DrawingML anchors, namespaces and content-type combinations outside the test corpus remain unverified.
Native XLSX export builds its supported package subset; it must not be used as a preservation editor for unsupported existing parts. Do not infer preservation from a successful native open/save cycle.

The package editor now bounds archive/expanded bytes (128 MiB each), part bytes (16 MiB), entry count (4096), expansion ratio (2000:1) and part-name shape. It still reports dropped directory/duplicate entries. The strict desktop importer rejects such drops and unsupported parts rather than silently reconstructing them. It uses namespace-aware XML parsing with DTDs prohibited, depth 64 and 200,000 elements.
