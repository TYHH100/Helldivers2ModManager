param([Parameter(Mandatory = $true)][string]$ZigExecutable)

$nativeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputRoot = Join-Path $nativeRoot 'bin\win-x64'
$outputPath = Join-Path $outputRoot 'jalium_texture.dll'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $ZigExecutable build-lib (Join-Path $nativeRoot 'texture.zig') -dynamic -O ReleaseFast `
    -target x86_64-windows "-femit-bin=$outputPath"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
