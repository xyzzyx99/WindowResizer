$ErrorActionPreference = 'Stop'

$version = $args[0]

if ($version -notmatch '\d\.\d\.\d') {
    Write-Output 'Error: Version not set correctly.'
    exit 1
}

Write-Host '>> current version: ', $version -ForegroundColor Green

# build
Write-Host '>> building...' -ForegroundColor Green
dotnet restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

dotnet publish .\src\WindowResizer.CLI\ -c Release -o publish\WindowResizer.CLI  /p:Version=$version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

function Import-VsDevCmdForNativeSelector
{
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $programFilesX86 = ${env:ProgramFiles}
    }

    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found: $vswhere"
    }

    $vsInstall = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsInstall)) {
        throw 'Could not find a Visual Studio installation with C++ build tools.'
    }

    $vsDevCmd = Join-Path $vsInstall 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path $vsDevCmd)) {
        throw "VsDevCmd.bat not found: $vsDevCmd"
    }

    Write-Host '>> importing Visual C++ build environment...' -ForegroundColor Green
    $cmd = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
    $envLines = & cmd.exe /d /s /c $cmd
    if ($LASTEXITCODE -ne 0) {
        throw 'VsDevCmd.bat failed.'
    }

    foreach ($line in $envLines) {
        if ($line -match '^([^=]+)=(.*)
$nativeSource = '.\src\WindowResizer.CLI.NativeSelector\NativeSelector.cpp'
$nativeOutput = '.\publish\WindowResizer.CLI\WindowResizer.Selector.Native.dll'
$nativeObj = '.\publish\WindowResizer.CLI\NativeSelector.obj'
if (Test-Path $nativeSource) {
    Write-Host '>> building native selector...' -ForegroundColor Green
    $clArgs = @(
        '/nologo',
        '/LD',
        '/EHsc',
        '/std:c++17',
        '/utf-8',
        '/O2',
        '/DUNICODE',
        '/D_UNICODE',
        '/DNOMINMAX',
        "/Fo:$nativeObj",
        $nativeSource,
        "/Fe:$nativeOutput",
        'user32.lib',
        'kernel32.lib'
    )

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        Import-VsDevCmdForNativeSelector
    }

    & cl.exe @clArgs
    if ($LASTEXITCODE -ne 0) { throw 'native selector build failed.' }
    Remove-Item .\publish\WindowResizer.CLI\NativeSelector.obj -ErrorAction SilentlyContinue
    Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.exp -ErrorAction SilentlyContinue
    Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.lib -ErrorAction SilentlyContinue
}
else {
    throw "native selector source not found: $nativeSource"
}

# release
$archive = "WindowResizer.CLI-$version.zip"
Write-Host ">> packing $archive..."

7z a .\Releases\$archive .\publish\WindowResizer.CLI\
if ($LASTEXITCODE -ne 0) { throw '7z archive failed.' }

Write-Host '>> done.' -ForegroundColor Green
) {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        throw 'cl.exe still not found after importing the Visual C++ build environment.'
    }
}
# native selector DLL
$nativeSource = '.\src\WindowResizer.CLI.NativeSelector\NativeSelector.cpp'
$nativeOutput = '.\publish\WindowResizer.CLI\WindowResizer.Selector.Native.dll'
$nativeObj = '.\publish\WindowResizer.CLI\NativeSelector.obj'
if (Test-Path $nativeSource) {
    Write-Host '>> building native selector...' -ForegroundColor Green
    $clArgs = @(
        '/nologo',
        '/LD',
        '/EHsc',
        '/std:c++17',
        '/utf-8',
        '/O2',
        '/DUNICODE',
        '/D_UNICODE',
        '/DNOMINMAX',
        "/Fo:$nativeObj",
        $nativeSource,
        "/Fe:$nativeOutput",
        'user32.lib',
        'kernel32.lib'
    )

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        Import-VsDevCmdForNativeSelector
    }

    & cl.exe @clArgs
    if ($LASTEXITCODE -ne 0) { throw 'native selector build failed.' }
    Remove-Item .\publish\WindowResizer.CLI\NativeSelector.obj -ErrorAction SilentlyContinue
    Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.exp -ErrorAction SilentlyContinue
    Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.lib -ErrorAction SilentlyContinue
}
else {
    throw "native selector source not found: $nativeSource"
}

# release
$archive = "WindowResizer.CLI-$version.zip"
Write-Host ">> packing $archive..."

7z a .\Releases\$archive .\publish\WindowResizer.CLI\
if ($LASTEXITCODE -ne 0) { throw '7z archive failed.' }

Write-Host '>> done.' -ForegroundColor Green

