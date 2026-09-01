# Build ViennaBar — setup de entorno VC++/SDK para NativeAOT en esta máquina.
# Uso: .\build.ps1 [-Publish] (por defecto build Release de toda la sln).
param(
    [switch]$Publish,
    [string]$Project = "src\ViennaBar.Spike.AppBar"
)

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$msvc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207"
$sdk = "C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0"

# NativeAOT necesita ucrt/um libs y link.exe en el entorno del proceso
$env:LIB = "$sdk\ucrt\x64;$sdk\um\x64;$msvc\lib\x64"
$env:PATH = "$msvc\bin\Hostx64\x64;$env:PATH"

if ($Publish) {
    & $dotnet publish $Project -c Release -r win-x64
} else {
    & $dotnet build ViennaBar.sln -c Release
}
exit $LASTEXITCODE
