# KHZ Sheet v0.1.0-preview.1

Development preview of the verified subset. This is not a production-readiness claim.

## Included
- Exact representable rational arithmetic with corrected wide-intermediate handling.
- Native formula/dependency rollback and bounded traversal corrections.
- Stronger ZIP/XLSX boundary checks and malformed-input regressions.
- Managed ABI verification fixes for concurrent first use and malformed native images.
- Independent managed formula scratch arena so persistent dependency edges survive scratch reuse.
- Windows WPF application branding and `Copy Proof` action.
- Optional post-quantum release packaging: ML-KEM-768 key encapsulation, ML-DSA-65 manifest signature, AES-256-GCM payload encryption.

## Verified locally before release
- ASAN+UBSAN native suite: 14/14.
- Portable native suite: 15/15.
- AVX2 native suite: 15/15.
- Independent rational oracle: 132,054 checks per portable and AVX2 library, zero failures.
- .NET solution: Release build, zero warnings/errors.
- Managed boundary modes: lifetime/reuse, 8-way ABI race, malformed-image handling all pass.
- WPF executable: builds and survives startup smoke test.
- PQC packaging: encrypt/decrypt/signature round-trip reproduces the original SHA-256.

## Explicit limitations
Arbitrary untrusted XLSX, financial-use suitability, integrity-sensitive records, controlled beta, broad end-user readiness, full localization/RTL, accessibility, tables and charts remain NOT VERIFIED or absent. See RELEASE-CHECKLIST.md.
