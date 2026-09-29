# OPC preservation
Managed `OpcPackage.cs` is separate from native XLSX reconstruction. Existing `KhzOpcPackage` tests exercise package editing and preservation. Untouched part payloads and ZIP-container bytes are different contracts: repackaging is not byte-identical ZIP preservation.
Use payload hashes to assess untouched parts. A successful managed OPC regression does not establish complete chart/table import, editing or rendering. Relationship graphs, DrawingML anchors, namespaces and content-type combinations outside the test corpus remain unverified.
Native XLSX export builds its supported package subset; it must not be used as a preservation editor for unsupported existing parts. Do not infer preservation from a successful native open/save cycle.
