@echo off
setlocal EnableDelayedExpansion
rem Puts Describer After Dark on GitHub, announcing each step as it goes.
rem Everything is also written to publish-log.txt in this folder.
rem Run build.cmd first, so the program exists to put in the download.
cd /d "%~dp0"
set "VERSION=1.0"
set "REPO=DescriberAfterDark"
set "LOG=%~dp0publish-log.txt"
echo Describer After Dark publish log, %DATE% %TIME% > "%LOG%"

call :say "Publishing Describer After Dark version %VERSION% to GitHub. There are 6 steps, and each one will be announced."
call :say "This creates a PUBLIC repository named %REPO% that anyone can see and download."
choice /c YN /m "Continue? Press Y for yes or N for no"
if errorlevel 2 (
  call :say "Stopped. Nothing was published."
  goto end
)

rem ---------------------------------------------------------------- 1
call :say "Step 1 of 6: Checking that the program has been built."
if not exist "DescriberAfterDark.exe" (
  call :say "Problem: DescriberAfterDark.exe isn't in this folder. Run build.cmd first, then run this again."
  goto end
)
call :say "Step 1 done. The program is built."

rem ---------------------------------------------------------------- 2
call :say "Step 2 of 6: Checking for Git and the GitHub tool."
where git >nul 2>nul
if errorlevel 1 (
  call :say "Git isn't installed. Installing it now. Windows may ask for permission; answer Yes. This can take a few minutes, and nothing more will be said until it finishes."
  winget install --id Git.Git --exact --silent --accept-source-agreements --accept-package-agreements >> "%LOG%" 2>&1
  call :say "Finished installing Git."
)
where gh >nul 2>nul
if errorlevel 1 (
  call :say "The GitHub tool isn't installed. Installing it now. Windows may ask for permission; answer Yes. This can take a few minutes, and nothing more will be said until it finishes."
  winget install --id GitHub.cli --exact --silent --accept-source-agreements --accept-package-agreements >> "%LOG%" 2>&1
  call :say "Finished installing the GitHub tool."
)
set "PATH=%ProgramFiles%\Git\cmd;%ProgramFiles%\GitHub CLI;%PATH%"
where git >nul 2>nul || (call :say "Problem: Git still can't be found. Close this window, open a new one, and run this again." & goto end)
where gh >nul 2>nul || (call :say "Problem: the GitHub tool still can't be found. Close this window, open a new one, and run this again." & goto end)
call :say "Step 2 done. Git and the GitHub tool are ready."

rem ---------------------------------------------------------------- 3
call :say "Step 3 of 6: Signing in to GitHub."
gh auth status >nul 2>nul
if errorlevel 1 (
  call :say "You need to sign in. Next, a line will show a one-time code with a dash in the middle, then ask you to press Enter."
  call :say "Read the code, press Enter, and your browser opens GitHub. Type the code there, choose Continue, then Authorize GitHub CLI. Then come back to this window."
  echo.
  gh auth login --web --git-protocol https --hostname github.com
  echo.
  gh auth status >nul 2>nul
  if errorlevel 1 (
    call :say "Problem: the sign-in didn't finish. Run this again to try once more."
    goto end
  )
)
for /f "delims=" %%i in ('gh api user --jq .login') do set "GHUSER=%%i"
for /f "delims=" %%i in ('gh api user --jq .id') do set "GHID=%%i"
call :say "Step 3 done. Signed in to GitHub as !GHUSER!."

rem ---------------------------------------------------------------- 4
call :say "Step 4 of 6: Preparing the source code."
if not exist ".git" git init -b main >> "%LOG%" 2>&1
rem Commits use GitHub's private no-reply address, so your real email isn't published.
git config user.name "!GHUSER!"
git config user.email "!GHID!+!GHUSER!@users.noreply.github.com"
git add -A >> "%LOG%" 2>&1
git diff --cached --quiet
if errorlevel 1 (
  git commit -q -m "Describer After Dark %VERSION%" >> "%LOG%" 2>&1
  call :say "Step 4 done. Your changes are ready to upload."
) else (
  call :say "Step 4 done. Nothing new to upload since last time."
)

rem ---------------------------------------------------------------- 5
call :say "Step 5 of 6: Uploading the source code to GitHub. This usually takes under a minute."
gh repo view "!GHUSER!/%REPO%" >nul 2>nul
if errorlevel 1 (
  call :say "Creating the %REPO% repository."
  gh repo create %REPO% --public --source . --remote origin --push --description "Uncensored, adults-only image describer for blind and visually impaired people. Runs locally on Windows." >> "%LOG%" 2>&1
  if errorlevel 1 (call :say "Problem: creating the repository failed. Send publish-log.txt for help." & goto end)
) else (
  git remote get-url origin >nul 2>nul || git remote add origin "https://github.com/!GHUSER!/%REPO%.git"
  git push -u origin main >> "%LOG%" 2>&1
  if errorlevel 1 (call :say "Problem: uploading failed. Send publish-log.txt for help." & goto end)
)
call :say "Step 5 done. The source code is on GitHub."

rem ---------------------------------------------------------------- 6
call :say "Step 6 of 6: Making the download for version %VERSION%. This can take a minute while it uploads."
call make-release.cmd nopause >> "%LOG%" 2>&1
if errorlevel 1 (call :say "Problem: making the download zip failed. Send publish-log.txt for help." & goto end)
gh release view "v%VERSION%" >nul 2>nul
if errorlevel 1 (
  gh release create "v%VERSION%" "release\DescriberAfterDark-%VERSION%-win-x64.zip" --title "Describer After Dark %VERSION%" --notes-file RELEASE_NOTES.md >> "%LOG%" 2>&1
) else (
  call :say "Version %VERSION% was already on GitHub, so its download is being replaced with this one."
  gh release upload "v%VERSION%" "release\DescriberAfterDark-%VERSION%-win-x64.zip" --clobber >> "%LOG%" 2>&1
)
if errorlevel 1 (call :say "Problem: uploading the download failed. Send publish-log.txt for help." & goto end)
call :say "Step 6 done. The download is up."

echo.
call :say "ALL DONE. Describer After Dark is published."
call :say "Your GitHub page is: https://github.com/!GHUSER!/%REPO%"
call :say "The download link to share is: https://github.com/!GHUSER!/%REPO%/releases/latest"

:end
echo.
echo Press any key to close this window.
pause >nul
exit /b

:say
echo %~1
echo %~1 >> "%LOG%"
exit /b 0
