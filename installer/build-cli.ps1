$ErrorActionPreference = 'Stop'

$version = $args[0]

if ($version -notmatch '\d\.\d\.\d') {
    Write-Output 'Error: Version not set correctly.'
    exit 1
}

Write-Host '>> current version: ' $version -ForegroundColor Green

# build
Write-Host '>> building...' -ForegroundColor Green
dotnet restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

dotnet publish .\src\WindowResizer.CLI\ -c Release -o .\publish\WindowResizer.CLI /p:Version=$version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# native selector DLL
$nativeSource = '.\src\WindowResizer.CLI.NativeSelector\NativeSelector.cpp'
$nativeOutput = '.\publish\WindowResizer.CLI\WindowResizer.Selector.Native.dll'
$nativeObj = '.\publish\WindowResizer.CLI\NativeSelector.obj'

if (!(Test-Path $nativeSource)) {
    throw "native selector source not found: $nativeSource"
}

function Import-VcBuildEnvironment {
    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ($cl) {
        Write-Host '>> cl.exe already available.' -ForegroundColor Green
        return
    }

    Write-Host '>> importing Visual C++ build environment...' -ForegroundColor Green

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path $vswhere)) {
        throw "vswhere.exe not found: $vswhere"
    }

    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsPath)) {
        throw 'could not find a Visual Studio installation with VC++ tools.'
    }

    $vsDevCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
    if (!(Test-Path $vsDevCmd)) {
        throw "VsDevCmd.bat not found: $vsDevCmd"
    }

    $envDump = cmd /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
    if ($LASTEXITCODE -ne 0) {
        throw 'VsDevCmd.bat failed.'
    }

    foreach ($line in $envDump) {
        $idx = $line.IndexOf('=')
        if ($idx -le 0) { continue }

        $name = $line.Substring(0, $idx)
        $value = $line.Substring($idx + 1)
        Set-Item -Path "Env:$name" -Value $value
    }

    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (!$cl) {
        throw 'cl.exe still not found after importing Visual C++ build environment.'
    }
}

Import-VcBuildEnvironment

Write-Host '>> building native selector...' -ForegroundColor Green
& cl.exe /nologo /LD /EHsc /std:c++17 /utf-8 /O2 /DUNICODE /D_UNICODE /DNOMINMAX /Fo:$nativeObj $nativeSource /Fe:$nativeOutput user32.lib kernel32.lib
if ($LASTEXITCODE -ne 0) { throw 'native selector build failed.' }

Remove-Item .\publish\WindowResizer.CLI\NativeSelector.obj -ErrorAction SilentlyContinue
Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.exp -ErrorAction SilentlyContinue
Remove-Item .\publish\WindowResizer.CLI\WindowResizer.Selector.Native.lib -ErrorAction SilentlyContinue

# release
$archive = "WindowResizer.CLI-$version.zip"
Write-Host ">> packing $archive..."

if (!(Test-Path .\Releases)) {
    New-Item -ItemType Directory -Path .\Releases | Out-Null
}

7z a .\Releases\$archive .\publish\WindowResizer.CLI\
if ($LASTEXITCODE -ne 0) { throw '7z archive failed.' }

Write-Host '>> done.' -ForegroundColor Green
