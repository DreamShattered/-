@echo off
setlocal

rem Build entry point for this project.
rem Wraps build.ps1 with -ExecutionPolicy Bypass because local script
rem execution is disabled by default on Windows.
rem
rem   build.cmd           publish a self-contained single-file exe into publish\
rem   build.cmd -Debug    debug build only (fast, needs .NET Desktop Runtime)

set "PS=powershell"
where pwsh >nul 2>nul
if %errorlevel%==0 set "PS=pwsh"

%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%errorlevel%"

echo.
if "%~1"=="" pause
exit /b %RC%
