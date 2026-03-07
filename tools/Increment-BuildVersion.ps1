Param(
    [Parameter(Mandatory=$true)] [string]$BuildNumberFile,
    [Parameter(Mandatory=$true)] [string]$OutputDir,
    [int]$Major = 1,
    [int]$Minor = 0
)

# Ensure build number file exists
if (-not (Test-Path $BuildNumberFile)) {
    New-Item -ItemType File -Path $BuildNumberFile -Force | Out-Null
    Set-Content -Path $BuildNumberFile -Value "0"
}

# Read, increment, save
$raw = (Get-Content -Path $BuildNumberFile -Raw).Trim()
[int]$build = 0
if (-not [int]::TryParse($raw, [ref]$build)) { $build = 0 }
$build++
Set-Content -Path $BuildNumberFile -Value $build

# Build the version strings
# AssemblyFileVersion requires 4 parts; we use Major.Minor.Build.0
$fileVersion = "{0}.{1}.{2}.0" -f $Major, $Minor, $build
$informational = "{0}.{1}.{2}" -f $Major, $Minor, $build

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Generate C# assembly attributes
$generatedFile = Join-Path $OutputDir "GeneratedVersionInfo.cs"
$code = @"
using System.Reflection;

[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$informational")]
"@

Set-Content -Path $generatedFile -Value $code -Encoding UTF8

Write-Host "Incremented build to $build -> FileVersion=$fileVersion"