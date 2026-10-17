@echo off
setlocal
rem Builds Describer After Dark. The finished DescriberAfterDark.exe is placed in this same folder, next to build.cmd.
rem If the .NET 8 SDK isn't installed, it installs it first using winget.
rem Everything is also written to build-log.txt, and the window waits for a key at the end.
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
echo Describer After Dark build log, %DATE% %TIME% > "%LOG%"

rem Running from inside the zip doesn't work: Windows extracts to a temporary folder that disappears.
echo %~dp0 | findstr /i "\\Temp\\" >nul
if not errorlevel 1 (
  call :say "It looks like build.cmd was started from inside the zip file. Extract the whole zip first, for example with Extract All, then run build.cmd from the extracted DescriberAfterDark folder."
  goto finish_fail
)

call :check_sdk
if not errorlevel 1 goto build

call :say "The .NET 8 SDK is needed to build Describer After Dark, and it isn't installed. Installing it now with winget. Windows may ask for permission; answer Yes. This can take several minutes."
winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-source-agreements --accept-package-agreements >> "%LOG%" 2>&1
if errorlevel 1 (
  call :say "The automatic install of the .NET SDK didn't work. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 , then run build.cmd again."
  goto finish_fail
)

rem The new install isn't on this window's PATH yet, so add the usual location.
set "PATH=%ProgramFiles%\dotnet;%PATH%"
call :check_sdk
if errorlevel 1 (
  call :say "The .NET SDK was installed, but this window can't see it yet. Run build.cmd again and it should work."
  goto finish_fail
)

:build
rem The program can't be replaced while it is running.
tasklist /fi "imagename eq DescriberAfterDark.exe" 2>nul | findstr /i "DescriberAfterDark.exe" >nul
if not errorlevel 1 (
  call :say "Describer After Dark is running. Exit it first from its system tray icon, then run build.cmd again."
  goto finish_fail
)

call :say "Building Describer After Dark. This takes a minute or two the first time."
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish ImageDescriber.App\ImageDescriber.App.csproj -c Release -o "%~dp0." >> "%LOG%" 2>&1
if errorlevel 1 goto build_failed
if not exist "DescriberAfterDark.exe" goto build_failed

rem Older versions built into a separate "publish" folder. It isn't needed any more.
if exist "publish\DescriberAfterDark.exe" rmdir /s /q "publish"
if exist "publish\ImageDescriber.exe" rmdir /s /q "publish"

echo. >> "%LOG%"
echo BUILD SUCCEEDED >> "%LOG%"
echo.
echo BUILD SUCCEEDED.
echo The program is DescriberAfterDark.exe, in this same folder: %~dp0
echo Keep prism.dll and the third-party-licenses folder next to it. Everything it needs is in this folder.
if exist "prism.dll" (echo Speech through your screen reader is included.) else (echo Warning: prism.dll is missing, so speech will be off.)
echo.
echo Press any key to close this window.
pause >nul
exit /b 0

:build_failed
echo. >> "%LOG%"
echo BUILD FAILED >> "%LOG%"
echo.
echo BUILD FAILED.
echo The errors are saved in build-log.txt in the ImageDescriber folder.
echo Send that file to whoever is helping you.
goto finish_wait

:finish_fail
echo BUILD DID NOT RUN >> "%LOG%"
:finish_wait
echo.
echo Press any key to close this window.
pause >nul
exit /b 1

:say
echo %~1
echo %~1 >> "%LOG%"
exit /b 0

:check_sdk
where dotnet >nul 2>nul || exit /b 1
dotnet --list-sdks 2>nul | findstr /r /b /c:"8\." /c:"9\." /c:"1[0-9]\." >nul || exit /b 1
exit /b 0
