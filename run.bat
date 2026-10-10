@echo off
setlocal
cd /d "%~dp0"
title Journal Launcher

set "EXE_PATH=bin\Debug\net8.0-windows10.0.26100.0\win-x64\Journal.exe"

if /i "%~1"=="/fast" goto :LAUNCH
if /i "%~1"=="-f" goto :LAUNCH

echo ========================================================
echo   Journal - Minimalist Immersive Journal Launcher
echo ========================================================
echo.
echo [1/2] Checking incremental build...
dotnet build -nologo -v q
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Build failed. Please check the error messages above.
    pause
    exit /b %errorlevel%
)

:LAUNCH
if exist "%EXE_PATH%" (
    echo [2/2] Launching Journal application...
    start "" "%EXE_PATH%"
    exit /b 0
) else (
    echo [ERROR] Executable not found at %EXE_PATH%
    echo Attempting full rebuild...
    dotnet build -c Debug
    if exist "%EXE_PATH%" (
        start "" "%EXE_PATH%"
        exit /b 0
    ) else (
        echo [ERROR] Build succeeded but Journal.exe was not found.
        pause
        exit /b 1
    )
)