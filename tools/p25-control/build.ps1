param([Parameter(Mandatory)][string]$Trunker)
$ErrorActionPreference = 'Stop'
$crate = $PSScriptRoot
$runtime = Join-Path $crate '..\..\p25-runtime'
$env:TRUNKER_SOURCE = (Resolve-Path -LiteralPath $Trunker).Path
$manifest = Join-Path $crate 'Cargo.toml'
& cargo build --release --locked --manifest-path $manifest
if ($LASTEXITCODE) { throw 'P25 decoder build failed' }
$metadata = (& cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1 --manifest-path $manifest) -join "`n" | ConvertFrom-Json
if ($LASTEXITCODE -or !$metadata) { throw 'Could not read Cargo metadata' }
$null = New-Item -ItemType Directory -Force -Path $runtime
Copy-Item -LiteralPath (Join-Path $metadata.target_directory 'release\spectraxia_p25.dll') -Destination $runtime
$notices = foreach ($pkg in $metadata.packages | Where-Object source | Sort-Object name,version) {
    "$($pkg.name) $($pkg.version) · $($pkg.license)"
    $licenses = Get-ChildItem -LiteralPath (Split-Path $pkg.manifest_path) -File | Where-Object Name -Match '^LICENSE|^COPYING|^NOTICE'
    if (!$licenses) { throw "Missing license for $($pkg.name)" }
    foreach ($file in $licenses) { $file.Name; [IO.File]::ReadAllText($file.FullName) }
}
$notices | Set-Content -LiteralPath (Join-Path $runtime 'Rust notices.txt') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $env:TRUNKER_SOURCE 'LICENSE') -Destination (Join-Path $runtime 'Trunker license.txt')
