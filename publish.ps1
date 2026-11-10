<# Produce a self-contained single-file release in .\publish\ #>

# dotnet on PATH may be a runtime without an SDK (for example
# C:\Program Files\dotnet). Publish needs the SDK, so probe:
# FILETABS_DOTNET, common install locations, then PATH.
$dotnet = $null
foreach ($candidate in @(
    $env:FILETABS_DOTNET,
    "D:\Programs\dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe"
)) {
    if ($candidate -and (Test-Path $candidate)) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and (& $cmd.Source --list-sdks 2>$null)) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Error "No dotnet with an SDK found. Set FILETABS_DOTNET to its full path."
    exit 1
}
Write-Host "using dotnet: $dotnet"

# Single-file publish pulls Microsoft.NET.ILLink.Tasks on first use. This
# machine has no NuGet sources configured, so restore explicitly against
# nuget.org; otherwise a fresh machine or a cleared package cache fails with
# NU1100 before publish ever starts.
& $dotnet restore FileTabs.csproj `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:EnableCompressionInSingleFile=false `
    --source "https://api.nuget.org/v3/index.json" `
    -v minimal

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish FileTabs.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none `
    -v minimal

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Published to $(Resolve-Path .\bin\Release\net10.0-windows\win-x64\publish)"
}
