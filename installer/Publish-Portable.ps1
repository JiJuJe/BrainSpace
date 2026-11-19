param(
    [string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [switch] $OfflineBuild
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$project = Join-Path $ProjectRoot 'BrainSpace.csproj'
$dist = Join-Path $ProjectRoot 'dist'
$packages = Join-Path $dist 'nuget'
$stage = Join-Path $dist 'portable\BrainSpace'
$licenses = Join-Path $stage 'licenses'

[void][IO.Directory]::CreateDirectory($packages)
$restoreArgs = @('restore', $project, '-r', 'win-x64', '-p:SelfContained=true', '--packages', $packages, '--nologo')
if ($OfflineBuild) { $restoreArgs += @('--source', $packages, '-p:NuGetAudit=false') }
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'The .NET runtime restore failed.' }

& dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore --nologo `
    -p:PublishSingleFile=false -p:DebugType=none "-p:RestorePackagesPath=$packages" -o $stage
if ($LASTEXITCODE -ne 0) { throw 'The self-contained .NET build failed.' }

[void][IO.Directory]::CreateDirectory($licenses)
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'README.md') -Destination $stage
$asset = Get-Content -LiteralPath (Join-Path $ProjectRoot 'obj\project.assets.json') -Raw | ConvertFrom-Json
foreach ($package in 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64') {
    $packageRoot = Join-Path $packages $package
    $download = $asset.project.frameworks.'net10.0-windows'.downloadDependencies | Where-Object name -eq $package
    if (!$download) { throw "The restore did not record the version of $package." }
    $version = ($download.version.Trim('[', ']') -split ',')[0].Trim()
    foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $packageRoot $version) -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($package + '-' + $notice.Name))
    }
}

$verification = Join-Path $dist 'portable-verification'
$app = Join-Path $stage 'BrainSpace.exe'
$test = Start-Process -FilePath $app -ArgumentList '--self-test', ('"' + $verification + '"') -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path $verification 'results.txt')
if ($test.ExitCode -ne 0) { throw 'The portable application failed its verification run.' }

$zip = Join-Path $dist 'BrainSpace-Windows-x64.zip'
$temporaryZip = $zip + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryZip, [IO.Compression.CompressionLevel]::Fastest, $true)
    [IO.File]::Move($temporaryZip, $zip, $true)
} finally {
    if ([IO.File]::Exists($temporaryZip)) { [IO.File]::Delete($temporaryZip) }
}
Get-Item -LiteralPath $zip | Select-Object FullName, Length
