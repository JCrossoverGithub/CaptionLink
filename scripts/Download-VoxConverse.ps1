[CmdletBinding()]
param(
    [ValidateSet("dev", "test")]
    [string] $Split = "dev",

    [string] $DatasetRoot = (
        Join-Path $PSScriptRoot "..\benchmark-data\VoxConverse"
    )
)

$ErrorActionPreference = "Stop"

$datasetPath = [System.IO.Path]::GetFullPath($DatasetRoot)
$downloadPath = Join-Path $datasetPath ".downloads"
$audioPath = Join-Path $datasetPath ("audio\" + $Split)
$rttmPath = Join-Path $datasetPath ("rttm\" + $Split)

$audioUrl = if ($Split -eq "dev") {
    "https://www.robots.ox.ac.uk/~vgg/data/voxconverse/data/voxconverse_dev_wav.zip"
} else {
    "https://www.robots.ox.ac.uk/~vgg/data/voxconverse/data/voxconverse_test_wav.zip"
}

$audioArchive = Join-Path $downloadPath ("voxconverse_" + $Split + "_wav.zip")
$annotationArchive = Join-Path $downloadPath "voxconverse-v0.3-annotations.zip"
$audioExtractPath = Join-Path $downloadPath ("audio-" + $Split)
$annotationExtractPath = Join-Path $downloadPath "annotations-v0.3"

New-Item -ItemType Directory -Force -Path $downloadPath | Out-Null
New-Item -ItemType Directory -Force -Path $audioPath | Out-Null
New-Item -ItemType Directory -Force -Path $rttmPath | Out-Null

if (-not (Test-Path $audioArchive)) {
    Write-Host "Downloading VoxConverse $Split audio..."
    Invoke-WebRequest -Uri $audioUrl -OutFile $audioArchive
}

if (-not (Test-Path $annotationArchive)) {
    Write-Host "Downloading VoxConverse v0.3 annotations..."
    Invoke-WebRequest `
        -Uri "https://github.com/joonson/voxconverse/archive/refs/heads/master.zip" `
        -OutFile $annotationArchive
}

Write-Host "Extracting audio..."
Expand-Archive -Path $audioArchive -DestinationPath $audioExtractPath -Force

Write-Host "Extracting annotations..."
Expand-Archive -Path $annotationArchive -DestinationPath $annotationExtractPath -Force

Get-ChildItem -Path $audioExtractPath -Filter "*.wav" -Recurse | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $audioPath -Force
}

$annotationSource = Join-Path $annotationExtractPath ("voxconverse-master\" + $Split)

if (-not (Test-Path $annotationSource)) {
    throw "The expected v0.3 annotation directory was not found: $annotationSource"
}

Get-ChildItem -Path $annotationSource -Filter "*.rttm" | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $rttmPath -Force
}

$wavCount = (Get-ChildItem -Path $audioPath -Filter "*.wav").Count
$rttmCount = (Get-ChildItem -Path $rttmPath -Filter "*.rttm").Count

Write-Host "VoxConverse $Split is ready."
Write-Host "WAV files:  $wavCount"
Write-Host "RTTM files: $rttmCount"
Write-Host "Audio: $audioPath"
Write-Host "RTTM:  $rttmPath"
