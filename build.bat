@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
title CutFlow - Build Installer

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "PROJECT=CutFlow.csproj"
set "PRIVATE_DOTNET=%CD%\.dotnet"
set "DOTNET_EXE="
set "ISCC_EXE="

echo ============================================================
echo  CUTFLOW - BUILD INSTALLER
echo ============================================================
echo.

if not exist "%PROJECT%" goto :missing_project

set "VAD_MODEL=Assets\silero_vad.onnx"
if not exist "%VAD_MODEL%" (
  echo [VOICE] Downloading pinned Silero VAD model...
  if not exist "Assets" mkdir "Assets"
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $u='https://raw.githubusercontent.com/snakers4/silero-vad/806dcba3f0b5d95282d0889a074954a2f8c6397b/src/silero_vad/data/silero_vad.onnx'; Invoke-WebRequest -UseBasicParsing $u -OutFile '%VAD_MODEL%'; if ((Get-Item '%VAD_MODEL%').Length -lt 500000) { throw 'VAD model download is unexpectedly small.' }"
  if errorlevel 1 goto :fail
)

for /f "delims=" %%V in ('dotnet --version 2^>nul') do set "GLOBAL_DOTNET_VERSION=%%V"
if defined GLOBAL_DOTNET_VERSION (
  for /f "tokens=1 delims=." %%M in ("!GLOBAL_DOTNET_VERSION!") do set "GLOBAL_MAJOR=%%M"
  if !GLOBAL_MAJOR! GEQ 8 set "DOTNET_EXE=dotnet"
)
if not defined DOTNET_EXE if exist "%PRIVATE_DOTNET%\dotnet.exe" set "DOTNET_EXE=%PRIVATE_DOTNET%\dotnet.exe"
if not defined DOTNET_EXE (
  echo [SDK] Installing private .NET 8 SDK...
  if not exist "%PRIVATE_DOTNET%" mkdir "%PRIVATE_DOTNET%"
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $p=Join-Path $env:TEMP 'cutflow-dotnet-install.ps1'; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $p; & $p -Channel 8.0 -InstallDir '%PRIVATE_DOTNET%' -NoPath"
  if errorlevel 1 goto :fail
  set "DOTNET_EXE=%PRIVATE_DOTNET%\dotnet.exe"
)

echo [SDK] Using:
"%DOTNET_EXE%" --version
if errorlevel 1 goto :fail

for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$x=Get-Content '%PROJECT%'; $x.Project.PropertyGroup.Version | Select-Object -First 1"`) do set "APP_VERSION=%%V"
if not defined APP_VERSION (
  echo [ERROR] Could not read app version from %PROJECT%.
  goto :fail
)
echo [VERSION] %APP_VERSION%

if exist "dist" rmdir /s /q "dist"
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
mkdir "dist\app" >nul 2>&1
mkdir "dist\installer" >nul 2>&1

echo.
echo [BUILD] Publishing CutFlow...
"%DOTNET_EXE%" publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%CD%\dist\app" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 goto :fail
if not exist "dist\app\CutFlow.exe" goto :fail

for %%P in ("%ProgramFiles%\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do if not defined ISCC_EXE if exist "%%~P" set "ISCC_EXE=%%~P"
if not defined ISCC_EXE for /f "delims=" %%P in ('where ISCC.exe 2^>nul') do if not defined ISCC_EXE set "ISCC_EXE=%%P"
if not defined ISCC_EXE (
  echo [ERROR] Inno Setup 6 was not found.
  echo Run setup.bat once, or install Inno Setup 6, then rerun build.bat.
  goto :fail
)

echo [INSTALLER] Using: %ISCC_EXE%
"%ISCC_EXE%" /Qp "/DAppVersion=%APP_VERSION%" "/DSourceDir=%CD%\dist\app" "installer\CutFlow.iss"
if errorlevel 1 goto :fail
set "INSTALLER=dist\installer\CutFlow-Setup-v%APP_VERSION%.exe"
if not exist "%INSTALLER%" (
  echo [ERROR] Inno Setup finished but %INSTALLER% was not created.
  goto :fail
)

for /f "delims=" %%H in ('powershell -NoProfile -Command "(Get-FileHash '%INSTALLER%' -Algorithm SHA256).Hash"') do set "INSTALLER_SHA=%%H"
>"dist\installer\SHA256SUMS.txt" echo !INSTALLER_SHA!  CutFlow-Setup-v%APP_VERSION%.exe

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo ============================================================
echo  Installer: %INSTALLER%
echo  Checksum:  dist\installer\SHA256SUMS.txt
echo.
if "%NO_PAUSE%"=="0" pause
exit /b 0

:missing_project
echo [ERROR] Missing %PROJECT%.
:fail
echo.
echo ============================================================
echo  BUILD FAILED
echo ============================================================
if "%NO_PAUSE%"=="0" pause
exit /b 1
