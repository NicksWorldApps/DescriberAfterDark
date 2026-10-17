@echo off
setlocal
rem Sets the version number for the next release. This is the only place the version needs changing:
rem the program, the download zip, and the GitHub release all take it from here.
cd /d "%~dp0"
set "PROJ=ImageDescriber.App\ImageDescriber.App.csproj"

for /f "delims=" %%v in ('powershell -NoProfile -Command "([regex]::Match((Get-Content -Raw '%PROJ%'), '<Version>([^<]+)</Version>')).Groups[1].Value"') do set "CURRENT=%%v"
echo The current version is %CURRENT%.
echo.
echo Type the new version number and press Enter.
echo Use three numbers separated by dots, higher than %CURRENT%. For example: a small fix after 1.1.0 is 1.1.1, new features are 1.2.0.
set "NEW="
set /p "NEW=New version: "
if "%NEW%"=="" (
  echo Nothing was changed.
  goto end
)

powershell -NoProfile -Command "if ('%NEW%' -notmatch '^\d+\.\d+\.\d+$') { exit 1 }; if ([version]'%NEW%' -le [version]'%CURRENT%') { exit 2 }; exit 0"
if errorlevel 2 (
  echo %NEW% isn't higher than the current version %CURRENT%, so people wouldn't be offered it as an update. Nothing was changed.
  goto end
)
if errorlevel 1 (
  echo %NEW% isn't a version number like 1.2.0. Nothing was changed.
  goto end
)

powershell -NoProfile -Command "$p='%PROJ%'; $t=Get-Content -Raw $p; $t=$t -replace '<Version>[^<]+</Version>','<Version>%NEW%</Version>'; [IO.File]::WriteAllText((Resolve-Path $p), $t)"
if errorlevel 1 (
  echo Changing the version failed.
  goto end
)

echo.
echo Done. The version is now %NEW%.
echo Next:
echo 1. Write what's new at the top of RELEASE_NOTES.md, under a heading like: ## What's new in %NEW%
echo    That text is shown to people when they're asked whether to update.
echo 2. Run build.cmd.
echo 3. Run publish-to-github.cmd. Everyone running Describer After Dark will then be offered the update.

:end
echo.
echo Press any key to close this window.
pause >nul
