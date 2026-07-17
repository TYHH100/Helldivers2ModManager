[CmdletBinding()]
param(
    [ValidateSet('beta', 'rc', 'stable')]
    [string]$Channel = 'stable',
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expected = '2.0.0'

$tagPattern = switch ($Channel) {
    'beta' { '^2\.0\.0-beta\.\d+$' }
    'rc' { '^2\.0\.0-rc\.\d+$' }
    'stable' { '^2\.0\.0$' }
}

if ($Version -notmatch $tagPattern) {
    throw "Version '$Version' does not match the $Channel release channel ($tagPattern)."
}

$project = [xml](Get-Content (Join-Path $repositoryRoot 'Helldivers2ModManager/Helldivers2ModManager.csproj') -Raw)
$propertyGroups = @($project.Project.PropertyGroup)
foreach ($property in 'ProductVersion', 'AssemblyVersion', 'FileVersion') {
    $value = ($propertyGroups | ForEach-Object { $_.$property } | Where-Object { $_ } | Select-Object -First 1).ToString()
    if ($value -ne $expected) {
        throw "$property in the WPF project is '$value', expected '$expected'."
    }
}

$appSource = Get-Content (Join-Path $repositoryRoot 'Helldivers2ModManager/App.xaml.cs') -Raw
if ($appSource -notmatch 'new\(2,\s*0,\s*0,\s*0\)') {
    throw 'App.xaml.cs Version constant is not 2.0.0.0.'
}

foreach ($relativePath in 'hd2mmt_nexus-download-interceptor/package.json', 'hd2mmt_nexus-download-interceptor/manifest.json') {
    $document = Get-Content (Join-Path $repositoryRoot $relativePath) -Raw | ConvertFrom-Json
    if ($document.version -ne $expected) {
        throw "$relativePath declares version '$($document.version)', expected '$expected'."
    }
}

$settingsSource = Get-Content (Join-Path $repositoryRoot 'Helldivers2ModManager/Services/SettingsService.cs') -Raw
if ($settingsSource -notmatch 'EnableBrowserIntegration\s*=\s*false' -or
    $settingsSource -notmatch 'EnableExperimentalRepair\s*=\s*false') {
    throw 'Release safety defaults must keep browser integration and experimental repair disabled.'
}

Write-Host "Release validation passed: channel=$Channel version=$Version"
