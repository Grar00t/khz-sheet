param(
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = $PSScriptRoot
$NativeBuildDir = Join-Path $RepoRoot "build\desktop-native"
$DesktopProject = Join-Path $RepoRoot "src\KHZ.Sheet.Desktop\KHZ.Sheet.Desktop.csproj"
$DesktopOutput = Join-Path $RepoRoot "src\KHZ.Sheet.Desktop\bin\Release\net8.0-windows"

cmake -S $RepoRoot -B $NativeBuildDir `
    -DKHZ_ENABLE_LEDGER=OFF `
    -DKHZ_BUILD_SELFTEST=OFF `
    -DKHZ_BUILD_TESTS=OFF `
    -DKHZ_BUILD_SHARED=ON `
    -DCMAKE_WINDOWS_EXPORT_ALL_SYMBOLS=ON `
    -DCMAKE_C_FLAGS="/std:c11 /experimental:c11atomics"

cmake --build $NativeBuildDir --config Release --target khz_sheet_shared
dotnet build $DesktopProject -c Release

$NativeDll = Get-ChildItem -Path $NativeBuildDir -Recurse -Filter "khz_sheet.dll" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $NativeDll) {
    throw "khz_sheet.dll was not produced by the native build."
}

New-Item -ItemType Directory -Force -Path $DesktopOutput | Out-Null
Copy-Item $NativeDll.FullName (Join-Path $DesktopOutput "khz_sheet.dll") -Force

if ($Run) {
    & (Join-Path $DesktopOutput "KHZ.Sheet.exe")
}
