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

# Clean any existing or stale assets
$assets = gh api "repos/$Repo/releases/$releaseId/assets" | ConvertFrom-Json
foreach ($a in $assets) {
    if ($a.name -eq $fileName -or $a.name -like "test*") {
        Write-Host "Deleting old asset: $($a.name) ($($a.id))..."
        gh api -X DELETE "repos/$Repo/releases/assets/$($a.id)" | Out-Null
    }
}

$fileLength = ([System.IO.FileInfo]$FilePath).Length
Write-Host "Uploading $fileName ($fileLength bytes) via curl..."
$uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=$fileName"
$authHeader = "Authorization: Bearer $token"

& curl.exe -sS --fail -X POST -H $authHeader -H "Content-Type: application/octet-stream" --data-binary "@$FilePath" $uploadUrl -o upload_result.json

if (Test-Path "upload_result.json") {
    $res = Get-Content "upload_result.json" -Raw | ConvertFrom-Json
    Remove-Item "upload_result.json" -Force -ErrorAction SilentlyContinue
    if ($res.size -eq $fileLength) {
        Write-Host "SUCCESS: $fileName verified ($($res.size) bytes, state: $($res.state))."
        exit 0
    } else {
        Write-Error "Uploaded size ($($res.size)) did not match local size ($fileLength)."
        exit 1
    }
} else {
    Write-Error "Upload output file missing."
    exit 1
}
