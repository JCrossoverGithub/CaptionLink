$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "CaptionLink development environment"
Write-Host "==============================="
Write-Host ""

Write-Host "[Git]"
git --version

Write-Host ""
Write-Host "[.NET]"
dotnet --version

Write-Host ""
Write-Host "[WSL]"
wsl --version
wsl --status

Write-Host ""
Write-Host "[WSL distributions]"
wsl -l -v

Write-Host ""
Write-Host "[NVIDIA / Windows]"
nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader

Write-Host ""
Write-Host "[WSL checks]"
wsl bash ./scripts/doctor-wsl.sh
