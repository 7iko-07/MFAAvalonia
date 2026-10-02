@echo off
setlocal EnableExtensions DisableDelayedExpansion
title MFAAvalonia - Windows x64 build
rem Double-click to publish. Use --no-pause when calling from a terminal or CI.
set "BUILD_EXIT_CODE=1"
pushd "%~dp0"
if errorlevel 1 goto finish

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet was not found. Install the .NET 10 SDK, then try again.
    goto failed
)

echo [1/3] Restoring desktop dependencies...
dotnet restore "MFAAvalonia.Desktop\MFAAvalonia.Desktop.csproj"
if errorlevel 1 goto failed

echo [2/3] Publishing Release for Windows x64...
dotnet publish "MFAAvalonia.Desktop\MFAAvalonia.Desktop.csproj" -c Release -r win-x64
if errorlevel 1 goto failed

echo [3/3] Checking publish output...
set "PUBLISH_DIR=%CD%\bin\AnyCPU\Release\win-x64\publish"
for %%F in (MFAAvalonia.exe interface.json) do (
    if not exist "%PUBLISH_DIR%\%%F" (
        echo [ERROR] Missing publish file: %%F
        goto failed
    )
)
for %%D in (libs runtimes plugins resource MaaAgentBinary) do (
    if not exist "%PUBLISH_DIR%\%%D\" (
        echo [ERROR] Missing publish directory: %%D
        goto failed
    )
    dir /b /a "%PUBLISH_DIR%\%%D" 2>nul | findstr . >nul
    if errorlevel 1 (
        echo [ERROR] Empty publish directory: %%D
        goto failed
    )
)
if not exist "%PUBLISH_DIR%\DependencySetup_*_win.bat" (
    echo [ERROR] Missing Windows dependency setup script.
    goto failed
)

echo.
echo Build succeeded.
echo EXE: "%PUBLISH_DIR%\MFAAvalonia.exe"
echo Keep the entire publish directory together when moving the application.
set "BUILD_EXIT_CODE=0"
goto cleanup

:failed
echo.
echo [ERROR] Build failed. See the error output above.

:cleanup
popd

:finish
echo.
if /i not "%~1"=="--no-pause" pause
exit /b %BUILD_EXIT_CODE%
