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

# native selector DLL
$nativeSource = '.\src\WindowResizer.CLI.NativeSelector\NativeSelector.cpp'
$nativeOutput = '.\publish\WindowResizer.CLI\WindowResizer.Selector.Native.dll'
$nativeObj = '.\publish\WindowResizer.CLI\NativeSelector.obj'
if (Test-Path $nativeSource) {
    Write-Host '>> building native selector...' -ForegroundColor Green
    & cl.exe /nologo /LD /EHsc /std:c++17 /utf-8 /O2 /DUNICODE /D_UNICODE /DNOMINMAX /Fo:$nativeObj $nativeSource /Fe:$nativeOutput user32.lib kernel32.lib
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
