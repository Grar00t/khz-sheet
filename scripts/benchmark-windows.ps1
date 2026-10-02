param(
    [string]$BuildDirectory = "build/verify",
    [string]$OutputDirectory = "build/performance"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot
try {
    $native = Join-Path $RepoRoot "$BuildDirectory/Release/khz_sheet.dll"
    if (!(Test-Path $native)) {
        throw "Native DLL not found at $native. Run scripts/verify-windows.ps1 first."
    }

    $project = "tests/dotnet/KhzPerformanceGate/KhzPerformanceGate.csproj"
    dotnet build $project -c Release -warnaserror | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Performance benchmark build failed." }

    $oldPath = $env:PATH
    $env:PATH = (Split-Path -Parent $native) + ";" + $oldPath
    try {
        $raw = dotnet run --project $project -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Performance benchmark failed." }
        $measurement = $raw | Select-Object -Last 1 | ConvertFrom-Json
        $out = Join-Path $RepoRoot $OutputDirectory
        New-Item -ItemType Directory -Force $out | Out-Null
        $receipt = [ordered]@{
            schema = 1
            commit = (git rev-parse HEAD)
            utc = [DateTime]::UtcNow.ToString("o")
            machine = [Environment]::MachineName
            os = [Environment]::OSVersion.VersionString
            dotnet = (dotnet --version)
            nativeDllSha256 = (Get-FileHash $native -Algorithm SHA256).Hash.ToLowerInvariant()
            benchmark = $measurement
        }
        $json = $receipt | ConvertTo-Json -Depth 8
        $path = Join-Path $out "receipt.json"
        [IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Write-Output $json
        Write-Output ("RECEIPT=" + $path)
    } finally {
        $env:PATH = $oldPath
    }
} finally {
    Pop-Location
}
