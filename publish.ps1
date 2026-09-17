$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDir 'NeteaseLyricsOverlay.csproj'
$publishDir = Join-Path $projectDir 'publish\win-x64'

Write-Host 'Publishing self-contained Windows x64 package...'
dotnet publish $projectFile `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -o $publishDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Published to: $publishDir"
