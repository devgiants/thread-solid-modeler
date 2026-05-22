$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'

& $msbuild "$repoRoot\ThreadModeler.sln" /t:Build /p:Configuration=Release /p:Platform='Any CPU' /p:TargetFrameworkVersion=v4.0 /p:FrameworkPathOverride='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

& powershell -ExecutionPolicy Bypass -File "$repoRoot\deploy-release.ps1"
