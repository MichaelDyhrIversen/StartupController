Param(
    [Parameter(Mandatory=$true)]
    [string]$VdprojPath,

    [Parameter(Mandatory=$true)]
    [string]$AssemblyPath,

    [switch]$UpdateProductCode
)

if (-not (Test-Path $VdprojPath)) {
    Write-Error "Setup project not found: $VdprojPath"
    exit 1
}
if (-not (Test-Path $AssemblyPath)) {
    Write-Error "Assembly not found: $AssemblyPath"
    exit 1
}

# Get product version from built assembly (FileVersion / ProductVersion)
$fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($AssemblyPath)
$rawVersion = $fv.ProductVersion

# strip any +metadata (e.g. 1.0.0+abcd...) to keep semver-like version
$clean = $rawVersion.Split('+')[0].Trim()

# Split into parts and normalize to 3 parts Major.Minor.Build
$parts = $clean -split '\.'

# Ensure we have at least 3 parts. If more than 3, fold extras into Build.
[int]$major = 0
[int]$minor = 0
[int]$build = 0

if ($parts.Length -ge 1) { [int]::TryParse($parts[0], [ref]$major) | Out-Null }
if ($parts.Length -ge 2) { [int]::TryParse($parts[1], [ref]$minor) | Out-Null }

if ($parts.Length -eq 3) {
    [int]::TryParse($parts[2], [ref]$build) | Out-Null
}
elseif ($parts.Length -gt 3) {
    # concatenate all remaining parts as digits for the build number
    $rest = $parts[2..($parts.Length - 1)] -join ''
    if (-not [int]::TryParse($rest, [ref]$build)) {
        # fallback: use 0 if parse fails
        $build = 0
    }
}
else {
    # less than 3 parts => build = 0
    $build = 0
}

# Clamp ranges to MSI requirements: Major and Minor 0..99, Build 0..9999
if ($major -lt 0) { $major = 0 } elseif ($major -gt 99) { $major = 99 }
if ($minor -lt 0) { $minor = 0 } elseif ($minor -gt 99) { $minor = 99 }
if ($build -lt 0) { $build = 0 } elseif ($build -gt 9999) { $build = 9999 }

$msiVersion = "{0}.{1}.{2}" -f $major, $minor, $build

Write-Host "Computed MSI ProductVersion: $msiVersion (from '$rawVersion')"

$content = Get-Content -Raw -Encoding UTF8 $VdprojPath

# Update ProductVersion line to format used by .vdproj (e.g. "ProductVersion" = "8:1.0.0")
if ($content -match '"ProductVersion"\s*=') {
    $content = [regex]::Replace($content, '("ProductVersion"\s*=\s*)".*?"', '$1"' + "8:$msiVersion" + '"', 'IgnoreCase')
} else {
    Write-Warning "ProductVersion entry not found in $VdprojPath"
}

# Generate new PackageCode GUID (must be new for each build)
$newPackageGuid = [guid]::NewGuid().ToString("B").ToUpper()
if ($content -match '"PackageCode"\s*=') {
    $content = [regex]::Replace($content, '("PackageCode"\s*=\s*)"8:\{.*?\}"', '$1"' + "8:$newPackageGuid" + '"', 'IgnoreCase')
} else {
    Write-Warning "PackageCode entry not found in $VdprojPath"
}

# Optionally update ProductCode (use with care — changing ProductCode affects upgrade rules)
if ($UpdateProductCode.IsPresent) {
    $newProductGuid = [guid]::NewGuid().ToString("B").ToUpper()
    if ($content -match '"ProductCode"\s*=') {
        $content = [regex]::Replace($content, '("ProductCode"\s*=\s*)"8:\{.*?\}"', '$1"' + "8:$newProductGuid" + '"', 'IgnoreCase')
        Write-Host "ProductCode updated to $newProductGuid"
    } else {
        Write-Warning "ProductCode entry not found in $VdprojPath"
    }
}

# Save file back
Set-Content -Path $VdprojPath -Value $content -Encoding UTF8

Write-Host "Updated $VdprojPath -> ProductVersion=8:$msiVersion, PackageCode=$newPackageGuid"