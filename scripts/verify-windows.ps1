param([string]$BuildDirectory = "build/verify", [switch]$WithPresentationOracle)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot
$OriginalPath = $env:PATH
$OriginalTemp = $env:TEMP
$OriginalTmp = $env:TMP
$OriginalArtifacts = $env:KHZ_TEST_ARTIFACTS
$Evidence = Join-Path $RepoRoot "build/evidence-windows"
New-Item -ItemType Directory -Force $Evidence | Out-Null
$env:TEMP = Join-Path $RepoRoot "build/compiler-temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$Steps = [Collections.Generic.List[object]]::new()
$Receipt = [ordered]@{
    schema = 1; platform = "Windows"; startedUtc = [DateTime]::UtcNow.ToString("o")
    baseCommit = (git rev-parse HEAD); sourceManifestSha256 = $null
    compilerWarnings = 0; native = $null; managedRunners = 7
    managedTestDiscovery = "Console assertion runners: dotnet run executes checks; dotnet test discovers no test-framework cases."
    steps = $Steps; success = $false
}
function Save-Receipt {
    $Receipt | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Evidence "receipt.json") -Encoding utf8NoBOM
}
function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $Evidence ($Name + ".log")
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $Executable @Arguments *> $log
    $code = $LASTEXITCODE
    $timer.Stop()
    $text = [IO.File]::ReadAllText($log).Replace($RepoRoot,"<repo>").Replace($env:TEMP,"<compiler-temp>")
    [IO.File]::WriteAllText($log,$text,[Text.UTF8Encoding]::new($false))
    $warnings = [regex]::Matches($text,'(?im)^.*\bwarning\s+(C\d+|CS\d+|MSB\d+|[A-Z]+\d+)\b').Count
    $Receipt.compilerWarnings += $warnings
    $Steps.Add([ordered]@{
        name = $Name; command = ($Executable + " " + ($Arguments -join " ")).Replace($RepoRoot,"<repo>")
        exitCode = $code; seconds = $timer.Elapsed.TotalSeconds; compilerWarnings = $warnings
        log = $Name + ".log"; sha256 = (Get-FileHash $log -Algorithm SHA256).Hash.ToLowerInvariant()
        summaries = @([regex]::Matches($text,'(?im)^.*(?:checks=\d+|failures=\d+|cases\s*=\s*\d+|tests passed).*') | ForEach-Object Value)
    })
    Save-Receipt
    Write-Output "$Name exit=$code compilerWarnings=$warnings"
    if ($code -ne 0 -or $warnings -ne 0) {
        Get-Content $log -Tail 35
        throw "Verification failed: $Name"
    }
}
try {
    $files = @(git ls-files --cached --others --exclude-standard) | Sort-Object -Unique |
        Where-Object { $_ -match '^(src/|include/|tests/|scripts/|\.github/|CMakeLists\.txt$|Directory.Build.props$|build-desktop.ps1$)' }
    $manifest = @($files | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
    $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Evidence "source-manifest.json") -Encoding utf8NoBOM
    $Receipt.sourceManifestSha256 = (Get-FileHash (Join-Path $Evidence "source-manifest.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    Invoke-Check "cmake-version" "cmake" @("--version")
    Invoke-Check "dotnet-version" "dotnet" @("--version")
    Invoke-Check "configure" "cmake" @("-S",".","-B",$BuildDirectory,"-DKHZ_ENABLE_LEDGER=AUTO","-DKHZ_BUILD_TESTS=ON","-DKHZ_BUILD_SHARED=ON","-DKHZ_STRICT_WARNINGS=ON")
    Invoke-Check "native-build" "cmake" @("--build",$BuildDirectory,"--config","Release","--parallel","4","--clean-first")
    $junit = Join-Path $Evidence "native-ctest.xml"
    Invoke-Check "native-ctest" "ctest" @("--test-dir",$BuildDirectory,"-C","Release","--output-on-failure","-V","--output-junit",$junit)
    [xml]$native = Get-Content $junit -Raw
    $Receipt.native = @{tests=[int]$native.testsuite.tests;failures=[int]$native.testsuite.failures;disabled=[int]$native.testsuite.disabled}
    Invoke-Check "desktop-build" "dotnet" @("build","src/KHZ.Sheet.Desktop/KHZ.Sheet.Desktop.csproj","-c","Release","-warnaserror")
    $dll = Join-Path $RepoRoot "$BuildDirectory/Release/khz_sheet.dll"
    $output = Join-Path $RepoRoot "src/KHZ.Sheet.Desktop/bin/Release/net8.0-windows"
    Copy-Item $dll (Join-Path $output "khz_sheet.dll") -Force
    $env:PATH = (Split-Path -Parent $dll) + ";" + $OriginalPath
    $env:KHZ_TEST_ARTIFACTS = Join-Path $Evidence "ui"
    foreach ($project in @("KhzExactLiteral","KhzFormulaCells","KhzDependencyRewrite","KhzOpcPackage","KhzDesktopContracts","KhzBoundaryContracts","KhzSubsystemContracts")) {
        $path = "tests/dotnet/$project/$project.csproj"
        Invoke-Check $project "dotnet" @("run","--project",$path,"-c","Release")
        Invoke-Check ($project+"-dotnet-test") "dotnet" @("test",$path,"-c","Release","--no-restore")
    }
    Invoke-Check "boundary-race" "dotnet" @("run","--project","tests/dotnet/KhzBoundaryContracts/KhzBoundaryContracts.csproj","-c","Release","--no-build","--","race",$dll)
    Invoke-Check "boundary-bad-image" "dotnet" @("run","--project","tests/dotnet/KhzBoundaryContracts/KhzBoundaryContracts.csproj","-c","Release","--no-build","--","bad-image")
    if ($WithPresentationOracle) {
        $artifactMatch = Select-String -Path (Join-Path $Evidence "KhzSubsystemContracts.log") -Pattern '^ARTIFACTS=(.+)$'
        $artifactPath = $artifactMatch.Matches[0].Groups[1].Value.Replace("<repo>",$RepoRoot)
        Invoke-Check "presentation-oracle" "python" @("tests/khz_presentation_oracle.py",$artifactPath)
    }
    $exe = Join-Path $output "KHZ.Sheet.exe"
    $process = Start-Process $exe -PassThru
    try {
        if (!$process.WaitForInputIdle(10000)) { throw "Desktop startup timeout" }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $process.Refresh()
            if ($process.HasExited -or ($process.MainWindowHandle -ne 0 -and $process.Responding)) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($process.HasExited -or $process.MainWindowHandle -eq 0 -or !$process.Responding) { throw "Desktop startup failed" }
        $Receipt.desktopSmoke = "PASS: release executable opened a responsive main window"
    } finally {
        if (!$process.HasExited) {
            [void]$process.CloseMainWindow()
            if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
        }
    }
    $Receipt.nativeDllSha256 = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $Receipt.desktopExeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $Receipt.success = $true
} finally {
    $Receipt.finishedUtc = [DateTime]::UtcNow.ToString("o")
    Save-Receipt
    $env:PATH=$OriginalPath; $env:TEMP=$OriginalTemp; $env:TMP=$OriginalTmp; $env:KHZ_TEST_ARTIFACTS=$OriginalArtifacts
    Pop-Location
}
