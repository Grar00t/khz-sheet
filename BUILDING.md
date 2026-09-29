# Building
From the repository root, Linux with GCC or Clang, CMake, Python for development fixtures, and SQLite development files:
```sh
cmake -S . -B build/release -DCMAKE_BUILD_TYPE=Release
cmake --build build/release --parallel 4
ctest --test-dir build/release --output-on-failure
```
Use `-DKHZ_ENABLE_LEDGER=OFF` when SQLite development files are unavailable. This disables ledger functionality explicitly. AVX2 is an explicit compile-time target, not runtime dispatch: `-DKHZ_ENABLE_AVX2=ON` requires an AVX2-capable CPU.
Windows desktop: run `pwsh -NoProfile -File build-desktop.ps1`. Installed PowerShell 7 executed this path; local Windows PowerShell 5 policy blocked the same script. No policy was weakened. The script builds the ledger-disabled native DLL and net8.0-windows WPF application.
Sanitizers: configure Debug with `-DKHZ_ENABLE_ASAN=ON -DKHZ_ENABLE_UBSAN=ON` using GCC/Clang. The MSVC sanitizer path is explicitly unsupported. Linux .NET execution requires a separately installed .NET 8-compatible SDK; it was absent in the WSL environment used here.
