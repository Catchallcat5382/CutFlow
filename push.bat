@echo off
setlocal EnableExtensions EnableDelayedExpansion
pushd "%~dp0" >nul
set "PROJECT_ROOT=%CD%"
set "GIT_DIR="
set "GIT_WORK_TREE="
set "GIT_INDEX_FILE="
git config --global --add safe.directory "%PROJECT_ROOT%" >nul 2>&1
call "%PROJECT_ROOT%\tools\release-settings.cmd"
title CutFlow - Push

git -C "%PROJECT_ROOT%" rev-parse --is-inside-work-tree >nul 2>&1 || (echo [ERROR] Run setup.bat first. & popd & pause & exit /b 1)
set "MSG=%*"
if not defined MSG set /p "MSG=Commit message [Update CutFlow]: "
if not defined MSG set "MSG=Update CutFlow"

git -C "%PROJECT_ROOT%" add .
if errorlevel 1 (echo [ERROR] git add failed. & popd & pause & exit /b 1)
git -C "%PROJECT_ROOT%" diff --cached --quiet
if errorlevel 1 (
  git -C "%PROJECT_ROOT%" commit -m "!MSG!"
  if errorlevel 1 (echo [ERROR] Commit failed. & popd & pause & exit /b 1)
) else (
  echo [GIT] No source changes to commit. Pushing current main anyway...
)

git -C "%PROJECT_ROOT%" push origin main
if errorlevel 1 (echo [ERROR] Push failed. & popd & pause & exit /b 1)

echo [OK] Pushed to https://github.com/%GITHUB_OWNER%/%GITHUB_REPO%
popd
pause
