@echo off
setlocal EnableExtensions EnableDelayedExpansion
pushd "%~dp0" >nul
set "PROJECT_ROOT=%CD%"
set "GIT_DIR="
set "GIT_WORK_TREE="
set "GIT_INDEX_FILE="
git config --global --add safe.directory "%PROJECT_ROOT%" >nul 2>&1
call "%PROJECT_ROOT%\tools\release-settings.cmd"
title CutFlow - Build + GitHub Release

echo ============================================================
echo  CUTFLOW - RELEASE INSTALLER
echo ============================================================

git -C "%PROJECT_ROOT%" rev-parse --is-inside-work-tree >nul 2>&1 || (echo [ERROR] Run setup.bat first. & popd & pause & exit /b 1)
where gh >nul 2>&1 || (echo [ERROR] GitHub CLI is missing. Run setup.bat. & popd & pause & exit /b 1)
gh auth status >nul 2>&1 || (echo [ERROR] GitHub CLI is not signed in. Run setup.bat. & popd & pause & exit /b 1)

call "%PROJECT_ROOT%\build.bat" --no-pause
if errorlevel 1 (echo [ERROR] Build failed. & popd & pause & exit /b 1)

for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$x=Get-Content '%PROJECT_ROOT%\CutFlow.csproj'; $x.Project.PropertyGroup.Version | Select-Object -First 1"`) do set "APP_VERSION=%%V"
set "TAG=v%APP_VERSION%"
set "INSTALLER=%PROJECT_ROOT%\dist\installer\CutFlow-Setup-v%APP_VERSION%.exe"
if not exist "%INSTALLER%" (echo [ERROR] Missing %INSTALLER%. & popd & pause & exit /b 1)

set "LOCAL_RELEASE=%PROJECT_ROOT%\releases\v%APP_VERSION%\CutFlow-Setup-v%APP_VERSION%.exe"
set "LOCAL_CHECKSUM=%PROJECT_ROOT%\releases\v%APP_VERSION%\SHA256SUMS.txt"
if not exist "%LOCAL_RELEASE%" (echo [ERROR] build.bat did not create %LOCAL_RELEASE%. & popd & pause & exit /b 1)
if not exist "%PROJECT_ROOT%\releases\latest\CutFlow-Setup.exe" (echo [ERROR] build.bat did not update releases\latest. & popd & pause & exit /b 1)

rem Commit/push the source state first. Build outputs/releases are ignored by git on purpose.
git -C "%PROJECT_ROOT%" add .
git -C "%PROJECT_ROOT%" diff --cached --quiet
if errorlevel 1 (
  git -C "%PROJECT_ROOT%" commit -m "Release %TAG%"
  if errorlevel 1 (echo [ERROR] Release commit failed. & popd & pause & exit /b 1)
)
git -C "%PROJECT_ROOT%" push origin main
if errorlevel 1 (echo [ERROR] Source push failed. & popd & pause & exit /b 1)

gh release view "%TAG%" -R "%GITHUB_OWNER%/%GITHUB_REPO%" >nul 2>&1
if errorlevel 1 (
  gh release create "%TAG%" "%LOCAL_RELEASE%#CutFlow Setup v%APP_VERSION%" "%LOCAL_CHECKSUM%#SHA-256 checksums" -R "%GITHUB_OWNER%/%GITHUB_REPO%" --title "CutFlow %TAG%" --generate-notes --latest
) else (
  gh release upload "%TAG%" "%LOCAL_RELEASE%#CutFlow Setup v%APP_VERSION%" "%LOCAL_CHECKSUM%#SHA-256 checksums" -R "%GITHUB_OWNER%/%GITHUB_REPO%" --clobber
  gh release edit "%TAG%" -R "%GITHUB_OWNER%/%GITHUB_REPO%" --latest
)
if errorlevel 1 (echo [ERROR] GitHub Release failed. Local installer is still in releases\latest. & popd & pause & exit /b 1)

echo.
echo ============================================================
echo  RELEASE COMPLETE
echo ============================================================
echo  releases\latest\CutFlow-Setup.exe
echo  GitHub tag: %TAG%
echo.
popd
pause
