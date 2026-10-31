<# Produce a self-contained single-file release in .\publish\ #>

# PATH 上的 dotnet 可能是无 SDK 的运行时（如 C:\Program Files\dotnet），
# publish 需要 SDK。按优先级探测：环境变量 → 常见安装位。
$dotnet = $null
foreach ($candidate in @(
    $env:FILETABS_DOTNET,
    "D:\Programs\dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe"
)) {
    if ($candidate -and (Test-Path $candidate)) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    # 最后兜底：PATH 上能跑通且带 SDK 的 dotnet。
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and (& $cmd.Source --list-sdks 2>$null)) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Error "未找到带 SDK 的 dotnet。可设置环境变量 FILETABS_DOTNET 指向完整路径。"
    exit 1
}
Write-Host "using dotnet: $dotnet"

& $dotnet publish FileTabs.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
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
