<# Produce a self-contained single-file release in .\publish\ #>

& "D:\Programs\dotnet\dotnet.exe" publish FileTabs.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -v minimal

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Published to $(Resolve-Path .\bin\Release\net10.0-windows\win-x64\publish)"
}
