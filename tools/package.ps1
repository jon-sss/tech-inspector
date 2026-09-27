# Builds the plugin in Release and zips it the way it installs: the DLLs at the root of the SimHub
# folder and the language file in its Languages folder, with the license alongside.
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-Sign]
#
# -Sign signs both DLLs with Azure Artifact Signing before zipping. It needs the Artifact Signing
# client tools, the Windows SDK's signtool, a metadata.json naming the signing account and
# certificate profile, and a signed-in Azure CLI. Point TI_SIGNING_METADATA and TI_SIGNING_DLIB at
# the metadata and Azure.CodeSigning.Dlib.dll if they aren't in the default places below.
param([switch]$Sign)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

if ($Sign) {
    $metadata = if ($env:TI_SIGNING_METADATA) { $env:TI_SIGNING_METADATA } else { Join-Path $env:USERPROFILE '.simpatico\signing\metadata.json' }
    $dlib = if ($env:TI_SIGNING_DLIB) { $env:TI_SIGNING_DLIB } else { Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll' }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
    foreach ($need in @($metadata, $dlib, $signtool)) { if (-not $need -or -not (Test-Path $need)) { throw "Signing needs $need" } }
    # The signing client asks the Azure CLI for a token, so az has to be on PATH.
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        $cli = "$env:ProgramFiles\Microsoft SDKs\Azure\CLI2\wbin"
        if (Test-Path $cli) { $env:PATH = "$cli;$env:PATH" } else { throw 'Signing needs the Azure CLI (az login).' }
    }
}

dotnet test (Join-Path $root 'tests\TechInspector.Core.Tests') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
dotnet build (Join-Path $root 'src\TechInspector.Plugin') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$bin = Join-Path $root 'src\TechInspector.Plugin\bin\Release\net48'
$stage = Join-Path $root "artifacts\TechInspector-$version"
$zip = "$stage.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'Languages') | Out-Null

$dlls = 'TechInspector.Plugin.dll', 'TechInspector.Core.dll' | ForEach-Object { Join-Path $stage $_ }
Copy-Item (Join-Path $bin 'TechInspector.Plugin.dll'), (Join-Path $bin 'TechInspector.Core.dll') $stage
Copy-Item (Join-Path $bin 'Languages\TechInspector.Plugin.resx') (Join-Path $stage 'Languages')
Copy-Item (Join-Path $root 'LICENSE.md') $stage

if ($Sign) {
    # Artifact Signing certificates last days, not years, so the timestamp is what keeps the
    # signature valid after the certificate expires.
    & $signtool sign /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $dlib /dmdf $metadata $dlls
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    foreach ($dll in $dlls) {
        $sig = Get-AuthenticodeSignature $dll
        if ($sig.Status -ne 'Valid' -or -not $sig.TimeStamperCertificate) { throw "$dll is not validly signed and timestamped: $($sig.StatusMessage)" }
        "Signed $(Split-Path $dll -Leaf): $($sig.SignerCertificate.GetNameInfo('SimpleName', $false)), timestamped"
    }
}

@"
Tech Inspector $version by Simpatico Sim Shop

1. Close SimHub.
2. Extract this zip into your SimHub folder (usually C:\Program Files (x86)\SimHub),
   so the two DLLs sit next to SimHubWPF.exe and the .resx file goes into Languages.
3. Start SimHub and enable Tech Inspector if it asks.

https://github.com/jon-sss/tech-inspector
"@ | Set-Content (Join-Path $stage 'INSTALL.txt') -Encoding utf8

# Zip entries use forward slashes; Compress-Archive in Windows PowerShell 5 writes backslashes,
# which some unzip tools turn into odd file names.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
"Packaged $zip"
"SHA-256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"
