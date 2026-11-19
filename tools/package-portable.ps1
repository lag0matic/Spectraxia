param(
    [string]$Release = (Get-Date -Format 'yyyy.MM.dd'),
    [string]$OutputDir,
    [string]$NativeDir = 'C:\sdrpp_windows_x64',
    [string]$VcRuntime = 'C:\sdrpp_windows_x64\vcruntime140.dll',
    [string]$NugetSource
)
$ErrorActionPreference = 'Stop'
$project = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '..')).FullName
if (!$OutputDir) { $OutputDir = Join-Path $project 'releases' }
if ($Release -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { throw 'Use a numeric dotted release version.' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$NativeDir = (Resolve-Path -LiteralPath $NativeDir).Path
$zip = Join-Path $OutputDir "Spectraxia-$Release-win-x64-portable.zip"
if (Test-Path -LiteralPath $zip) { throw "Release already exists: $zip" }
foreach ($name in 'rtlsdr.dll','libusb-1.0.dll','pthreadVC2.dll','hackrf.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $NativeDir $name) -PathType Leaf)) { throw "Missing $name" }
}
if (!(Test-Path -LiteralPath $VcRuntime -PathType Leaf)) { throw 'Missing x64 Visual C++ runtime.' }
$stage = Join-Path $project ('bin\package-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'Spectraxia'
$null = New-Item -ItemType Directory -Path $app
$argsPublish = @('publish', (Join-Path $project 'Spectraxia.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableSingleFileAnalyzer=false', '-p:DebugType=None', '-p:UseSharedCompilation=false',
    "-p:SdrDriverDir=$NativeDir", "-p:Version=$Release", '-o', $app)
if ($NugetSource) { $argsPublish += @('--source', $NugetSource, '-p:NuGetAudit=false') }
& dotnet @argsPublish
if ($LASTEXITCODE) { throw 'Portable publish failed.' }
Copy-Item -LiteralPath $VcRuntime -Destination (Join-Path $app 'vcruntime140.dll')
Set-Content -LiteralPath (Join-Path $app 'portable.flag') -Value 'Spectraxia portable settings' -Encoding ascii
Copy-Item -LiteralPath (Join-Path $project 'packaging\Start here.txt') -Destination $app
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
$docs = New-Item -ItemType Directory -Path (Join-Path $app 'docs')
Copy-Item -LiteralPath (Join-Path $project 'packaging\Receiver guide.md') -Destination $docs
$notices = New-Item -ItemType Directory -Path (Join-Path $docs 'notices')
Get-ChildItem -LiteralPath (Join-Path $project 'packaging\notices') -File | Copy-Item -Destination $notices
$cfg = Get-Content -Raw -LiteralPath (Join-Path $project 'bin\Release\net10.0-windows\win-x64\Spectraxia.runtimeconfig.json') | ConvertFrom-Json
$assets = Get-Content -Raw -LiteralPath (Join-Path $project 'obj\project.assets.json') | ConvertFrom-Json
foreach ($framework in $cfg.runtimeOptions.includedFrameworks) {
    $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version
    $pack = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $package } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$pack) { throw "Missing runtime notices for $package" }
    Get-ChildItem -LiteralPath $pack -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices ($framework.name + '-' + $_.Name)) }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$reference = [IO.Compression.ZipFile]::OpenRead((Join-Path $project 'AircraftReference.zip'))
try {
    foreach ($entry in 'LICENSE','SOURCE') {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($reference.GetEntry($entry), (Join-Path $notices "aircraft-reference-$entry.txt"))
    }
} finally { $reference.Dispose() }
foreach ($file in 'Spectraxia.exe','rtlsdr.dll','hackrf.dll','pthreadVC2.dll','libusb-1.0.dll','hd-runtime\libnrsc5.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "Missing published file $file" }
}
$null = New-Item -ItemType Directory -Force -Path $OutputDir
$pending = Join-Path $stage 'package.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $pending, [IO.Compression.CompressionLevel]::Optimal, $true)
Move-Item -LiteralPath $pending -Destination $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ascii
$verifiedStage = (Resolve-Path -LiteralPath $stage).Path
if ((Split-Path $verifiedStage -Parent) -eq (Join-Path $project 'bin') -and (Split-Path $verifiedStage -Leaf) -match '^package-[0-9a-f]{32}$') {
    Remove-Item -LiteralPath $verifiedStage -Recurse -Force
}
Write-Output $zip
