# SovereignSpreadsheetEngine: evidence-gated KHZ Sheet decision

Status: **EXPERIMENT / NOT PRODUCTION VERIFIED**
Date: 2026-10-16
Base repository: https://github.com/Grar00t/khz-sheet
Target: one existing C11 native spreadsheet engine, not a separate replacement product.

## Beneficiary and problem

Prospective beneficiary: offline spreadsheet operators handling logistical
or property-related records, as described by the supplied architecture JSON.
Observed technical requirement: address a 100,000-row x 1,000-column
worksheet with a limited number of populated cells, explicitly reject
capacity overflow, and measure the native core's outbound network behavior.

No user field study, actual ERP contract, production threat audit, or
representative workload has been supplied; tangible operational benefit is
UNMEASURED. The JSON is a proposal, NOT deployment evidence.

## Chosen scope (SOV-01)

Preserve the existing KHZ C11 rational/formula engine, optional SQLite
proof ledger, bounded XLSX subset, and .NET 8 WPF UI. Do not replace them
with Univer/Tauri until a direct GUI benchmark justifies the change.

New native CTest exercises:
- 100,000 different row indices (0..99,999).
- 1,000 different column indices (0..999).
- 100,000 populated cells, one per row, across 100 million possible
  coordinates. This is NOT 100 million populated cells.
- Exact reads of selected numeric values; one missing-cell lookup.
- Capacity ceiling: 100,001st distinct cell is rejected, without
  changing the cell or commit counters.
- Out-of-domain coordinate rejection at the existing engine's real
  row/column maximum.
- Bounded commit history audit, including the older records it dropped.

The program performs no XLSX IO, GUI rendering, ERP requests or AI calls.

## Observed on x86-64 WSL, GCC 15.2.0 (2026-10-16)

- CMake Release build, SQLite ledger explicitly disabled for this test.
- Native CTest SOV-01: PASS, 0.14 s total CTest elapsed, one run only.
- Synthetic range: 100,000 rows x 1,000 columns.
- 100,000 populated cells.
- Arena reservation: 134,217,728 bytes (128 MiB).
- Arena used: 25,534,336 bytes (about 24.35 MiB).
- 1 extra unique-cell write correctly rejected.
- Retained commit events examined: 65,536.
- Older events dropped from ring: 34,464. A successful audit does
  NOT claim full history retention across 100,000 commits.
- Full native CTest suite: 18/18 PASS with the new test.
- Separate Debug build with AddressSanitizer + UndefinedBehaviorSanitizer:
  new scale test 1/1 PASS; elapsed 1.58 s. This does not prove all
  memory safety boundaries are covered.
- Linux strace network-category syscall trace on ONLY the SOV-01
  native binary: empty; 0 network-category syscalls observed.
- No test of Windows WPF, WebView2, Tauri, Univer, browser service
  workers, background OS processes, ERP API, or plugins was done.
- Runtime results are hardware- and build-specific, NOT an FPS SLA.
- Neither Burp Suite nor a packet capture over the entire Windows
  application was run. No blanket zero-telemetry attestation.

To repeat native tests:

    cmake -S . -B build/sovereign-gate -DCMAKE_BUILD_TYPE=Release -DKHZ_ENABLE_LEDGER=OFF
    cmake --build build/sovereign-gate --parallel 4
    ctest --test-dir build/sovereign-gate --output-on-failure
    bash scripts/verify-native-network.sh build/sovereign-gate/khz_sovereign_scale_test

The last command intentionally requires strace and fails closed when
it is unavailable. Its result applies ONLY to that executable on
Linux, not to the Windows desktop or any external renderer.

## Corrections to the proposed architecture

1. UNIVERSAL PERFORMANCE CLAIM: **NOT VERIFIED.** Univer documents a
   Canvas2D engine for sheet cells, and React/DOM for menus and other
   chrome. Its 50-60 FPS marketing/benchmarks do not certify our
   hardware, workbook, viewport, formula density, or UI. References:
   https://docs.univer.ai/blog/rendering
   https://docs.univer.ai/blog/this-is-univer
   https://docs.univer.ai/

2. GRAPH OPTIMIZATIONS: **NOT IMPLEMENTED HERE.** TACO is real research
   on tabular-locality formula-graph compression. The reported near-98%
   reduction relates to edges across a specified GitHub dataset, not
   an engineering bound for arbitrary KHZ worksheets. A sampled
   workbook's formula topology and compressed graph correctness must
   be benchmarked before adopting TACO. Source:
   https://arxiv.org/abs/2302.05482
   https://www2.eecs.berkeley.edu/Pubs/TechRpts/2022/EECS-2022-67.pdf
   The parallel spreadsheet work matching the supplied speculation
   and thread-local wording is Puncalc (not "PUNK"):
   https://link.springer.com/article/10.1007/s11227-019-02823-8
   KHZ currently has its own deterministic dependency engine; no
   speculative parallel task scheduler was inserted in this change.

3. NETWORK POLICY: **NOT ATTESTED.** "telemetry bytes = 0" does not
   mean all outbound traffic = 0. A permitted logistics ERP endpoint
   is business egress. Define distinct counters (app telemetry vs
   authorized ERP traffic), immutable endpoint allowlist, TLS identity,
   record-level least privilege, and OS-level native socket policy.
   A browser CSP alone cannot enforce native process egress; a
   loopback Burp proxy with custom root CA is a testing mechanism,
   not an enterprise compliance certificate or production control.
   The supplied internal domain was NOT contacted or allowlisted.

4. CLOUD: **NO PERPETUAL ENTERPRISE GUARANTEE.** OCI Always Free A1 is
   quota/capacity/region constrained; not a substitute for an
   availability agreement and secured backups. Official source:
   https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm

5. LAW AND POLICY: **COMPLIANCE NOT ESTABLISHED.** PDPL Article 29
   governs transfers/disclosures of personal data outside Saudi Arabia
   under specified legal conditions and protections. ECC 2:2024 is
   a control framework, not a library compilation option. Whether
   these rules apply, and which controls have been assessed, requires
   a qualified entity-specific legal and security audit.
   Sources:
   https://dgp.sdaia.gov.sa/
   https://nca.gov.sa/ar/regulatory-documents/controls-list/ecc/
   https://nca.gov.sa/ar/regulatory-documents/controls-list/ccc/
   No Burp DAST result was provided or produced.

6. INTEGRATIONS: **NOT AUTHORIZED OR VERIFIED.** No Oracle EBS/ERP
   REST schema, auth model, supported endpoint, role/permission
   mapping or test environment was supplied. Do not invent write
   endpoints or implement the proposed "REST injection".
   Start with authorized, read-only integration and parameterized
   queries if a legitimate contract is made available.

7. UI / CI: **NO MIGRATION DEMONSTRATED.** Keep WPF and GitHub Actions
   already in this repository until profiler traces or operational
   requirements justify a tested alternative. Tauri, WebView2 and
   Univer license/security boundaries would need their own audits.
   A scanner PWA and Gemini/HF calls are NOT offline by default.

## Remaining gates to consider before production

- End-to-end Windows traffic observation over clean startup, workbook
  open/edit/save, and shutdown, with OS-level capture and outbound
  deny by default. State which version/hash and all process children.
- Representative workbook replay and 60 FPS verification over an
  actual screen at a specified percentiles/viewport/hardware with
  scroll, edit, recalc and memory monitored separately.
- Threat/permissions assessment, ERP transfer data classification,
  formal PDPL/ECC control mapping, Burp DAST on authorized endpoints,
  software-bill-of-materials/license check, recovery and backups.
- Representative real-user workflow measurement. None performed here.

CAUSALITY: NOT ESTABLISHED for claims that switching renderer,
database, scheduler or cloud would improve real operational outcomes.

NEXT EXPERIMENT: supply a synthetic, realistic sparse workbook and
test one actual Windows WPF scroll/edit/save scenario, collecting
frame times and outbound process traffic at a recorded commit.
Only a demonstrated failure should trigger a renderer migration.
