@echo off
setlocal
rem Packs the built program into a zip people can download and run without building anything.
rem Run build.cmd first. The zip is created in the "release" folder.
cd /d "%~dp0"
rem The version number comes from the built program itself (set by <Version> in ImageDescriber.App.csproj),
rem so the release always matches the program. The automatic updater relies on this.
set "VERSION=unknown"
if exist "DescriberAfterDark.exe" for /f "delims=" %%v in ('powershell -NoProfile -Command "$v=(Get-Item 'DescriberAfterDark.exe').VersionInfo; '{0}.{1}.{2}' -f $v.FileMajorPart,$v.FileMinorPart,$v.FileBuildPart"') do set "VERSION=%%v"
set "ZIP=release\DescriberAfterDark-%VERSION%-win-x64.zip"

if not exist "DescriberAfterDark.exe" (
  echo DescriberAfterDark.exe isn't here yet. Run build.cmd first, then run make-release.cmd again.
  goto end_fail
)
if not exist "prism.dll" (
  echo prism.dll is missing next to DescriberAfterDark.exe. Run build.cmd again.
  goto end_fail
)

if not exist release mkdir release
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path 'DescriberAfterDark.exe','prism.dll','third-party-licenses','README.md' -DestinationPath '%ZIP%' -Force"
if not exist "%ZIP%" (
  echo Making the zip failed.
  goto end_fail
)

echo.
echo RELEASE ZIP READY: %~dp0%ZIP%
echo It contains DescriberAfterDark.exe, prism.dll, the third-party-licenses folder, and the README.
if "%~1"=="nopause" exit /b 0
echo Press any key to close this window.
pause >nul
exit /b 0

:end_fail
if "%~1"=="nopause" exit /b 1
echo Press any key to close this window.
pause >nul
exit /b 1
