$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$installerScript = Join-Path $root 'installer\EveMarketRouteFinder.iss'
$installerDir = Join-Path $root 'artifacts\installer'
$msiScript = Join-Path $root 'installer\EveMarketRouteFinder.wxs'
$msiDir = Join-Path $root 'artifacts\msi'
$msiPath = Join-Path $msiDir 'EveMarketRouteFinder.msi'

foreach ($directory in @($publishDir, $installerDir, $msiDir)) {
    if (Test-Path $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

dotnet publish (Join-Path $root 'EveParserAvalonia.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

Get-ChildItem $publishDir -Filter '*.pdb' -File | Remove-Item -Force
New-Item -ItemType Directory -Force -Path $msiDir | Out-Null

$wix = Get-Command wix.exe -ErrorAction SilentlyContinue
if ($wix) {
    & $wix.Source build $msiScript `
        -arch x64 `
        -d "PublishDir=$publishDir" `
        -out $msiPath
    if ($LASTEXITCODE -eq 0) {
        Get-ChildItem $msiDir -Filter '*.wixpdb' -File | Remove-Item -Force
        Write-Host "MSI: $msiPath"
    } else {
        Write-Host "MSI build failed. If this is the WiX EULA prompt, run: wix eula"
    }
} else {
    Write-Host "WiX CLI is not installed. Install it with: dotnet tool install --global wix"
}

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $knownInnoPaths = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )

    $isccPath = $knownInnoPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
} else {
    $isccPath = $iscc.Source
}

if ($isccPath) {
    & $isccPath $installerScript
    Write-Host "Installer: $(Join-Path $root 'artifacts\installer\EveMarketRouteFinderSetup.exe')"
} else {
    Write-Host "Publish ready: $publishDir"
    Write-Host "Inno Setup compiler is not installed. Install Inno Setup 6 and run this script again to build the installer."
}
