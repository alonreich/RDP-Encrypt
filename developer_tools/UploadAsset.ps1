param(
    [string]$Repo = "alonreich/RDP-Encrypt",
    [string]$Tag = "v2026.09.17",
    [string]$FilePath = "compiled\RDPVault.exe",
    [string]$LocalHash = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $FilePath)) {
    Write-Error "File $FilePath not found"
    exit 1
}

$fileName = [System.IO.Path]::GetFileName($FilePath)
$token = (gh auth token).Trim()
if (-not $token) {
    Write-Error "gh auth token not found"
    exit 1
}

$release = $null
try {
    $json = gh api "repos/$Repo/releases/tags/$Tag" 2>$null
    if ($LASTEXITCODE -eq 0 -and $json) {
        $release = $json | ConvertFrom-Json
    }
} catch { }

$notes = "Self-contained single-file win-x64 build published by build.cmd on $Tag. This is the only supported download."
if ($LocalHash) { $notes += " SHA256 $LocalHash" }

if (-not $release) {
    Write-Host "Creating release $Tag on GitHub..."
    $release = gh api "repos/$Repo/releases" -X POST -f tag_name="$Tag" -f name="RDP Vault $Tag" -f body="$notes" -F draft=false -F prerelease=false | ConvertFrom-Json
}

$releaseId = $release.id
Write-Host "Target Release ID: $releaseId"

$fileLength = ([System.IO.FileInfo]$FilePath).Length
Write-Host "Uploading $fileName ($fileLength bytes) via gh release upload..."

$maxRetries = 4
$attempt = 0
$uploadOk = $false
while (-not $uploadOk -and $attempt -lt $maxRetries) {
    $attempt++
    gh release upload "$Tag" "$FilePath" --repo "$Repo" --clobber
    if ($LASTEXITCODE -eq 0) {
        $uploadOk = $true
    } else {
        if ($attempt -lt $maxRetries) {
            Write-Host "Upload attempt $attempt failed, retrying in 4 seconds..."
            Start-Sleep -Seconds 4
        }
    }
}
if (-not $uploadOk) {
    Write-Error "gh release upload failed after $maxRetries attempts."
    exit 1
}

$assets = gh api "repos/$Repo/releases/$releaseId/assets" | ConvertFrom-Json
$uploaded = $assets | Where-Object { $_.name -eq $fileName } | Select-Object -First 1
if ($uploaded -and $uploaded.size -eq $fileLength) {
    Write-Host "SUCCESS: $fileName verified ($($uploaded.size) bytes, state: $($uploaded.state))."
    exit 0
} else {
    Write-Error "Uploaded size mismatch or asset not found for $fileName."
    exit 1
}
