@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title CutFlow - Diagnostic Launch

set "EXE=%CD%\releases\latest\CutFlow.exe"
if not exist "%EXE%" set "EXE=%CD%\dist\CutFlow.exe"

if not exist "%EXE%" (
  echo [ERROR] CutFlow.exe was not found.
  echo Run build.bat first.
  echo.
  pause
  exit /b 1
)

echo Launching:
echo %EXE%
echo.
"%EXE%"
set "CODE=%ERRORLEVEL%"

if "%CODE%"=="0" exit /b 0

echo.
echo [ERROR] CutFlow exited with code %CODE%.
set "LOGDIR=%LOCALAPPDATA%\CutFlow\Logs"
if exist "%LOGDIR%" (
  for /f "delims=" %%F in ('dir /b /a-d /o-d "%LOGDIR%\crash-*.log" 2^>nul') do (
    echo Opening crash log: %%F
    notepad "%LOGDIR%\%%F"
    goto :shown
  )
)

echo No CutFlow crash log was found.
:shown
echo.
pause
exit /b %CODE%
