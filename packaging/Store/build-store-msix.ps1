[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$storeDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $storeDirectory "..\.."))
$projectPath = Join-Path $projectRoot "CDisplayEx.CSharp.csproj"
$manifestTemplate = Join-Path $storeDirectory "Package.appxmanifest"
$assetDirectory = Join-Path $storeDirectory "Assets"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "release\store"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$assemblyVersionText = [string]$project.Project.PropertyGroup.AssemblyVersion
try {
    $parsedPackageVersion = [version]$assemblyVersionText
}
catch {
    throw "AssemblyVersion '$assemblyVersionText' is not a valid four-part package version."
}
$packageVersion = $parsedPackageVersion.ToString(4)

$sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction Stop |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "x64\makeappx.exe")) } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" }
if (-not $makeAppx) {
    throw "MakeAppx.exe was not found. Install the Windows 10/11 SDK."
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workingDirectory = [System.IO.Path]::GetFullPath((Join-Path $tempRoot (
    "FastReaderViewer.Store." + [Guid]::NewGuid().ToString("N"))))
if (-not $workingDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create Store staging outside the system temporary directory."
}
$payloadDirectory = Join-Path $workingDirectory "payload"

try {
    New-Item -ItemType Directory -Force -Path $payloadDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    & dotnet publish $projectPath `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $payloadDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $payloadAssets = Join-Path $payloadDirectory "Assets"
    Copy-Item -LiteralPath $assetDirectory -Destination $payloadAssets -Recurse
    $manifest = (Get-Content -LiteralPath $manifestTemplate -Raw).
        Replace("__PACKAGE_VERSION__", $packageVersion)
    Set-Content -LiteralPath (Join-Path $payloadDirectory "AppxManifest.xml") `
        -Value $manifest -Encoding utf8

    Get-ChildItem -LiteralPath $payloadDirectory -Filter "*.pdb" -File |
        Remove-Item -Force

    $packagePath = Join-Path $OutputDirectory (
        "FastReaderViewer_{0}_x64.msix" -f $packageVersion)
    $makeAppxOutput = & $makeAppx pack /o /d $payloadDirectory /p $packagePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        $makeAppxOutput | Write-Host
        throw "MakeAppx failed with exit code $LASTEXITCODE."
    }

    $package = Get-Item -LiteralPath $packagePath
    Write-Host "Microsoft Store package created:"
    Write-Host "  $($package.FullName)"
    Write-Host "  Version: $packageVersion"
    Write-Host "  Size: $($package.Length) bytes"
    Write-Host "The Store will sign this MSIX after certification."
}
finally {
    if ((Test-Path -LiteralPath $workingDirectory) -and
        $workingDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}
