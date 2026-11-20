param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'dist' }
$stage = Join-Path $PSScriptRoot ('.build/' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $stage 'release'
New-Item -ItemType Directory -Path $release,$OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path

foreach ($file in 'Branding.cs','Program.cs','TargetWindows.cs','LaunchSession.cs','ShortcutStore.cs','RestoreWindow.cs','RestoreEntry.cs') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src/$file") -Destination $stage
}
foreach ($file in 'index.html','style.css','intro.js','portable.js','pearl.webp') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "web/$file") -Destination $stage
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework C# compiler was not found.'
}
function Invoke-Compiler([string]$CompilerArguments) {
    $stdout = Join-Path $stage 'compile.txt'
    $stderr = Join-Path $stage 'compile-errors.txt'
    $process = Start-Process -FilePath $compiler -ArgumentList $CompilerArguments -WorkingDirectory $stage -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $stdout,$stderr
        throw 'The portable launcher could not be built.'
    }
}
$arguments = '/nologo /codepage:65001 /target:winexe /optimize+ /out:release\Launcher.exe ' +
    '/reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll ' +
    '/resource:index.html,StartupAnimation.index.html /resource:style.css,StartupAnimation.style.css /resource:intro.js,StartupAnimation.intro.js ' +
    '/resource:portable.js,StartupAnimation.portable.js /resource:pearl.webp,StartupAnimation.pearl.webp ' +
    'Branding.cs Program.cs TargetWindows.cs LaunchSession.cs ShortcutStore.cs RestoreWindow.cs'
Invoke-Compiler $arguments
Invoke-Compiler '/nologo /codepage:65001 /target:winexe /optimize+ /out:release\RestoreIntro.exe /reference:System.Windows.Forms.dll Branding.cs RestoreEntry.cs'

$native = Join-Path $release 'ChatGpt Startup Animation.exe'
$undo = Join-Path $release '撤销片头.exe'
Move-Item -LiteralPath (Join-Path $release 'Launcher.exe') -Destination $native
Move-Item -LiteralPath (Join-Path $release 'RestoreIntro.exe') -Destination $undo
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination $release
$zip = Join-Path $output 'ChatGpt-Startup-Animation-v1-Windows.zip'
if (Test-Path -LiteralPath $zip) {
    $zip = Join-Path $output ('ChatGpt-Startup-Animation-v1-Windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
}
Compress-Archive -LiteralPath $native,$undo,(Join-Path $release '使用说明.txt') -DestinationPath $zip
Get-Item -LiteralPath $zip | Select-Object FullName,Length
