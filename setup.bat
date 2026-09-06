@echo off
setlocal EnableExtensions EnableDelayedExpansion
pushd "%~dp0" >nul
set "PROJECT_ROOT=%CD%"
set "GIT_DIR="
set "GIT_WORK_TREE="
set "GIT_INDEX_FILE="
title CutFlow - First Time Setup
call "%PROJECT_ROOT%\tools\release-settings.cmd"

echo ============================================================
echo  CUTFLOW - FIRST TIME SETUP
echo ============================================================
echo.
echo [ROOT] %PROJECT_ROOT%
echo.

where git >nul 2>&1 || (
  echo [ERROR] Git is required. Install Git for Windows, then run setup.bat again.
  popd & pause & exit /b 1
)

where gh >nul 2>&1
if errorlevel 1 (
  echo [SETUP] GitHub CLI is missing. Trying winget...
  where winget >nul 2>&1 || (echo [ERROR] GitHub CLI ^(gh^) is required and winget was not found. & popd & pause & exit /b 1)
  winget install --id GitHub.cli --exact --silent --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (echo [ERROR] GitHub CLI install failed. & popd & pause & exit /b 1)
  set "PATH=%PATH%;%ProgramFiles%\GitHub CLI"
)

gh auth status >nul 2>&1
if errorlevel 1 (
  echo [GITHUB] Sign in once in the browser...
  gh auth login
  if errorlevel 1 (echo [ERROR] GitHub sign-in was not completed. & popd & pause & exit /b 1)
)
gh auth setup-git >nul 2>&1

for /f "delims=" %%U in ('gh api user --jq .login 2^>nul') do set "AUTH_OWNER=%%U"
for /f "delims=" %%I in ('gh api user --jq .id 2^>nul') do set "AUTH_ID=%%I"
if defined AUTH_OWNER set "GITHUB_OWNER=!AUTH_OWNER!"

echo [GITHUB] Owner: !GITHUB_OWNER!
echo [GITHUB] Repo:  !GITHUB_REPO!
echo.

if not exist "%PROJECT_ROOT%\releases" mkdir "%PROJECT_ROOT%\releases"
if not exist "%PROJECT_ROOT%\releases\latest" mkdir "%PROJECT_ROOT%\releases\latest"

rem Git for Windows can reject repositories on secondary drives as unsafe/dubious.
rem Mark this exact project path safe before testing or repairing .git.
echo [GIT] Verifying repository metadata...
git config --global --add safe.directory "%PROJECT_ROOT%" >nul 2>&1

set "GIT_DIAG=%TEMP%\cutflow-git-check-%RANDOM%%RANDOM%.txt"
if not exist "%PROJECT_ROOT%\.git" (
  echo [GIT] Initializing local repository...
  call :InitRepo
  if errorlevel 1 goto :SetupFailed
) else (
  git -C "%PROJECT_ROOT%" rev-parse --is-inside-work-tree >nul 2>"!GIT_DIAG!"
  if errorlevel 1 (
    rem Retry once after safe.directory in case this was a dubious-ownership block.
    git config --global --add safe.directory "%PROJECT_ROOT%" >nul 2>&1
    git -C "%PROJECT_ROOT%" rev-parse --is-inside-work-tree >nul 2>"!GIT_DIAG!"
  )
  if errorlevel 1 (
    echo [GIT] Existing .git metadata is incomplete or unreadable.
    if exist "!GIT_DIAG!" (
      echo [GIT] Git diagnostic:
      type "!GIT_DIAG!"
      echo.
    )
    echo [GIT] Repairing Git metadata. Your CutFlow source files will NOT be deleted.
    call :BackupBrokenGit
    if errorlevel 1 goto :SetupFailed
    call :InitRepo
    if errorlevel 1 goto :SetupFailed
  )
)

if exist "!GIT_DIAG!" del /q "!GIT_DIAG!" >nul 2>&1

git -C "%PROJECT_ROOT%" rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Git still cannot open the repaired repository.
  echo         Your source files were left untouched.
  goto :SetupFailed
)

git -C "%PROJECT_ROOT%" branch -M main >nul 2>&1

rem Give this repo a local commit identity when Git has none configured yet.
git -C "%PROJECT_ROOT%" config user.name >nul 2>&1
if errorlevel 1 if defined AUTH_OWNER git -C "%PROJECT_ROOT%" config user.name "!AUTH_OWNER!"
git -C "%PROJECT_ROOT%" config user.email >nul 2>&1
if errorlevel 1 (
  if defined AUTH_ID (
    git -C "%PROJECT_ROOT%" config user.email "!AUTH_ID!+!AUTH_OWNER!@users.noreply.github.com"
  ) else if defined AUTH_OWNER (
    git -C "%PROJECT_ROOT%" config user.email "!AUTH_OWNER!@users.noreply.github.com"
  )
)

echo [GIT] Preparing local source...
git -C "%PROJECT_ROOT%" add .
if errorlevel 1 (echo [ERROR] git add failed. & goto :SetupFailed)
git -C "%PROJECT_ROOT%" diff --cached --quiet
if errorlevel 1 (
  git -C "%PROJECT_ROOT%" commit -m "Set up CutFlow project and installer workflow"
  if errorlevel 1 (echo [ERROR] Initial local commit failed. & goto :SetupFailed)
)

rem Create the remote by name only, then attach it ourselves.
gh repo view "!GITHUB_OWNER!/!GITHUB_REPO!" >nul 2>&1
if errorlevel 1 (
  echo [GITHUB] Creating !GITHUB_VISIBILITY! repository !GITHUB_OWNER!/!GITHUB_REPO!...
  if /I "!GITHUB_VISIBILITY!"=="public" (
    gh repo create "!GITHUB_OWNER!/!GITHUB_REPO!" --public --description "!GITHUB_DESCRIPTION!"
  ) else (
    gh repo create "!GITHUB_OWNER!/!GITHUB_REPO!" --private --description "!GITHUB_DESCRIPTION!"
  )
  if errorlevel 1 (
    gh repo view "!GITHUB_OWNER!/!GITHUB_REPO!" >nul 2>&1
    if errorlevel 1 (echo [ERROR] Could not create GitHub repository. & goto :SetupFailed)
  )
) else (
  echo [GITHUB] Repository already exists. Reusing it.
)

set "REMOTE_URL=https://github.com/!GITHUB_OWNER!/!GITHUB_REPO!.git"
git -C "%PROJECT_ROOT%" remote get-url origin >nul 2>&1
if errorlevel 1 (
  git -C "%PROJECT_ROOT%" remote add origin "!REMOTE_URL!"
) else (
  git -C "%PROJECT_ROOT%" remote set-url origin "!REMOTE_URL!"
)
if errorlevel 1 (echo [ERROR] Could not configure the origin remote. & goto :SetupFailed)

rem Make sure Inno Setup is available for installer builds.
set "ISCC_FOUND="
for %%P in ("%ProgramFiles%\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do if exist "%%~P" set "ISCC_FOUND=1"
if not defined ISCC_FOUND where ISCC.exe >nul 2>&1 && set "ISCC_FOUND=1"
if not defined ISCC_FOUND (
  echo [INSTALLER] Inno Setup 6 was not found. Trying winget...
  where winget >nul 2>&1 || (echo [ERROR] Install Inno Setup 6, then run setup.bat again. & goto :SetupFailed)
  winget install --id JRSoftware.InnoSetup --exact --silent --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (echo [ERROR] Inno Setup install failed. & goto :SetupFailed)
)

echo [GITHUB] Pushing main...
git -C "%PROJECT_ROOT%" push -u origin main
if errorlevel 1 (echo [ERROR] Initial GitHub push failed. & goto :SetupFailed)

echo.
echo [BUILD] Creating the current CutFlow installer and local release folders...
call "%PROJECT_ROOT%\build.bat" --no-pause
if errorlevel 1 (
  echo [ERROR] GitHub setup succeeded, but the installer build failed.
  echo         Fix the build error and run build.bat again.
  goto :SetupFailed
)

echo.
echo ============================================================
echo  SETUP COMPLETE
echo ============================================================
echo  Local:   %PROJECT_ROOT%
echo  GitHub:  https://github.com/!GITHUB_OWNER!/!GITHUB_REPO!
echo  Build:   build.bat
echo  Push:    push.bat
echo  Release: release.bat
echo  Installer: releases\latest\CutFlow-Setup.exe
echo.
echo GitHub/release settings live in tools\release-settings.cmd
popd
pause
exit /b 0

:InitRepo
git -C "%PROJECT_ROOT%" init -b main >nul 2>&1
if errorlevel 1 git -C "%PROJECT_ROOT%" init
if errorlevel 1 (
  echo [ERROR] git init failed.
  exit /b 1
)
git config --global --add safe.directory "%PROJECT_ROOT%" >nul 2>&1
git -C "%PROJECT_ROOT%" branch -M main >nul 2>&1
exit /b 0

:BackupBrokenGit
set "BACKUP_ROOT=%LOCALAPPDATA%\CutFlow\SetupBackups"
for /f "delims=" %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%T"
if not defined STAMP set "STAMP=%RANDOM%%RANDOM%"
if not exist "!BACKUP_ROOT!" mkdir "!BACKUP_ROOT!" >nul 2>&1
set "BACKUP_PATH=!BACKUP_ROOT!\git-metadata-!STAMP!"
echo [GIT] Backing up broken metadata to:
echo       !BACKUP_PATH!
powershell -NoProfile -ExecutionPolicy Bypass -Command "$src = Join-Path $env:PROJECT_ROOT '.git'; $dst = $env:BACKUP_PATH; if (Test-Path -LiteralPath $src) { Move-Item -LiteralPath $src -Destination $dst -Force }" >nul 2>&1
if exist "%PROJECT_ROOT%\.git" (
  echo [ERROR] Could not move the broken .git metadata out of the project.
  echo         Close terminals/tools using the repository and run setup.bat again.
  exit /b 1
)
exit /b 0

:SetupFailed
if exist "!GIT_DIAG!" del /q "!GIT_DIAG!" >nul 2>&1
popd
pause
exit /b 1
