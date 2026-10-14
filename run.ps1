<#
.SYNOPSIS
    Jot - Minimalist Immersive Journal Launcher
.DESCRIPTION
    Builds (incremental) and launches the unpackaged WinUI 3 application.
.PARAMETER Fast
    Skips dotnet build and launches Jot.exe immediately.
#>

param(
    [switch]$Fast
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$ExePath = Join-Path $ScriptDir "bin\Debug\net8.0-windows10.0.26100.0\win-x64\Jot.exe"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Jot - Minimalist Immersive Journal Launcher" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

if (-not $Fast) {
    Write-Host "[1/2] Checking incremental build..." -ForegroundColor Yellow
    dotnet build -nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`n[ERROR] Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        return
    }
} else {
    Write-Host "[1/2] Fast mode: skipping build check" -ForegroundColor Gray
}

if (Test-Path $ExePath) {
    Write-Host "[2/2] Launching Jot application..." -ForegroundColor Green
    Start-Process -FilePath $ExePath
    Write-Host "Jot started successfully!" -ForegroundColor Green
} else {
    Write-Host "[ERROR] Executable not found at $ExePath" -ForegroundColor Red
    Write-Host "Attempting full rebuild..." -ForegroundColor Yellow
    dotnet build -c Debug
    if (Test-Path $ExePath) {
        Start-Process -FilePath $ExePath
    } else {
        Write-Host "[ERROR] Executable still not found after build." -ForegroundColor Red
    }
}