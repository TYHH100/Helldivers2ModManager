param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(1, 1.25, 1.5, 2)]
    [double]$ExpectedScale,
    [string]$AppPath = "",
    [string]$Page = "Dashboard",
    [int]$Width = 800,
    [int]$Height = 600,
    [switch]$FullMatrix
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..\")).Path
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $repository "Helldivers2ModManager\bin\Release\net8.0-windows\win-x64\Helldivers2ModManager.exe"
}
$AppPath = (Resolve-Path $AppPath).Path
$env:HD2MM_RUN_UI_TESTS = "1"
$env:HD2MM_APP_PATH = $AppPath
$env:HD2MM_EXPECTED_DPI_SCALE = $ExpectedScale.ToString([Globalization.CultureInfo]::InvariantCulture)
if (-not $FullMatrix) {
    $env:HD2MM_UI_DPI_CASE = "{0}@{1}x{2}" -f $Page, $Width, $Height
}
else {
    Remove-Item Env:HD2MM_UI_DPI_CASE -ErrorAction SilentlyContinue
}

try {
    dotnet test (Join-Path $repository "Helldivers2ModManager.UiTests\Helldivers2ModManager.UiTests.csproj") `
        -c Release --no-build --no-restore
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:HD2MM_UI_DPI_CASE -ErrorAction SilentlyContinue
    Remove-Item Env:HD2MM_EXPECTED_DPI_SCALE -ErrorAction SilentlyContinue
    $targets = Get-ChildItem $env:TEMP -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "Helldivers2ModManager.UiTests*" -or $_.Name -like "hd2mm-*"
    }
    foreach ($target in $targets) {
        Remove-Item -LiteralPath $target.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}
