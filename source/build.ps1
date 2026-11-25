param([switch]$Tests)
$ErrorActionPreference='Stop'
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf=Join-Path $framework 'WPF'
$output=Join-Path (Split-Path $PSScriptRoot -Parent) 'MusicProbe.exe'
$references=@('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:'+(Join-Path $wpf $_) }
$arguments=@('/nologo','/optimize+','/target:winexe','/platform:x64',('/out:'+$output),('/win32manifest:'+(Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:'+(Join-Path $PSScriptRoot 'app.ico')),('/resource:'+(Join-Path $PSScriptRoot 'Ui.xaml')+',Ui.xaml'))+$references+@((Join-Path $PSScriptRoot 'Engine.cs'),(Join-Path $PSScriptRoot 'Program.cs'))
& (Join-Path $framework 'csc.exe') $arguments
if($LASTEXITCODE -ne 0){throw 'Build failed'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination ($output+'.config') -Force
if($Tests){
 $testOutput=Join-Path $PSScriptRoot 'MusicProbe.Tests.exe'
 $testArgs=@('/nologo','/optimize+','/target:exe','/platform:x64','/main:MusicProbe.Tests',('/out:'+$testOutput),('/resource:'+(Join-Path $PSScriptRoot 'Ui.xaml')+',Ui.xaml'))+$references+@((Join-Path $PSScriptRoot 'Engine.cs'),(Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'Tests.cs'))
 & (Join-Path $framework 'csc.exe') $testArgs
 if($LASTEXITCODE -ne 0){throw 'Test build failed'}
 & $testOutput
 if($LASTEXITCODE -ne 0){throw 'Tests failed'}
}
Write-Output "Ready: $output"
