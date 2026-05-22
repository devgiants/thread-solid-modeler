$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $repoRoot 'bin\Debug\Contents'
$packageContents = Join-Path $repoRoot 'bin\Debug\PackageContents.xml'
$sourceAddin = Join-Path $buildDir 'ThreadSolidModeler.Inventor.addin'
$sourceDll = Join-Path $buildDir 'ThreadModeler.dll'
$bundleRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\ThreadSolidModeler.bundle'
$targetContentsDir = Join-Path $bundleRoot 'Contents'
$targetAddin = Join-Path $targetContentsDir 'ThreadSolidModeler.Inventor.addin'
$targetDll = Join-Path $targetContentsDir 'ThreadModeler.dll'
$targetPackageContents = Join-Path $bundleRoot 'PackageContents.xml'

if (-not (Test-Path $sourceAddin)) {
    throw "Missing add-in file: $sourceAddin"
}

if (-not (Test-Path $sourceDll)) {
    throw "Missing build output DLL: $sourceDll"
}

if (-not (Test-Path $packageContents)) {
    throw "Missing package contents file: $packageContents"
}

if (-not (Test-Path $bundleRoot)) {
    New-Item -ItemType Directory -Path $bundleRoot | Out-Null
}

if (-not (Test-Path $targetContentsDir)) {
    New-Item -ItemType Directory -Path $targetContentsDir | Out-Null
}

$itemsToCopy = Get-ChildItem -Path $buildDir -Force
foreach ($item in $itemsToCopy) {
    try {
        if ($item.PSIsContainer) {
            Copy-Item -LiteralPath $item.FullName -Destination $targetContentsDir -Recurse -Force
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination $targetContentsDir -Force
    }
    catch {
        Write-Host "Skipping locked or unavailable item: $($item.FullName)"
    }
}

try {
    Copy-Item -LiteralPath $packageContents -Destination $targetPackageContents -Force
}
catch {
    Write-Host "Skipping package contents copy: $packageContents"
}

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($sourceAddin)

$assemblyNode = $xml.SelectSingleNode('/Addin/Assembly')
if ($null -eq $assemblyNode) {
    throw "Could not find /Addin/Assembly in $sourceAddin"
}

$assemblyNode.InnerText = $targetDll

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$writerSettings = New-Object System.Xml.XmlWriterSettings
$writerSettings.Encoding = $utf8NoBom
$writerSettings.Indent = $true
$writerSettings.NewLineChars = "`r`n"
$writerSettings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

$writer = [System.Xml.XmlWriter]::Create($targetAddin, $writerSettings)
try {
    $xml.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Host "Deployed test add-in to: $targetAddin"
Write-Host "Assembly points to: $targetDll"
