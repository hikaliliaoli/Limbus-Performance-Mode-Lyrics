$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDir 'NeteaseLyricsOverlay.csproj'
$publishedExe = Join-Path $projectDir 'publish\win-x64\NeteaseLyricsOverlay.exe'
$developmentExe = Join-Path $projectDir 'bin\Release\net9.0-windows10.0.22621.0\NeteaseLyricsOverlay.exe'

if ((Test-Path $developmentExe) -and
    (!(Test-Path $publishedExe) -or
     (Get-Item $developmentExe).LastWriteTimeUtc -gt (Get-Item $publishedExe).LastWriteTimeUtc)) {
    $exe = $developmentExe
} elseif (Test-Path $publishedExe) {
    $exe = $publishedExe
} else {
    Write-Host 'First launch: building Netease Lyrics Overlay...'
    dotnet build $projectFile -c Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $exe = $developmentExe
}

Start-Process -FilePath $exe -WorkingDirectory $projectDir
