param([switch]$Test,[switch]$TestsOnly,[string]$OutputDirectory='dist')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$out = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Path $out -Force | Out-Null
$refs = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Security.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object { $p=Join-Path $wpf $_; if (!(Test-Path $p)) { $p=Join-Path $framework $_ }; '/r:' + $p }
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName
if (!$TestsOnly) {
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /resource:"$root\src\Main.xaml",Main.xaml /win32manifest:"$root\app.manifest" /out:"$out\OneLineReader.exe" @refs @sources
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $out -Force
}
if ($Test -or $TestsOnly) {
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /target:exe /platform:anycpu /codepage:65001 /main:OneLine.Tests /out:"$out\OneLineReader.Tests.exe" @refs @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
    & "$out\OneLineReader.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
if (!$TestsOnly) { Get-Item -LiteralPath "$out\OneLineReader.exe" | Select-Object FullName,Length }
