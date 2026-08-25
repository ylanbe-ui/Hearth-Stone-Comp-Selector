@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "ROOT=%~dp0"
set "LOG=%ROOT%install.log"
>"%LOG%" echo ===== HDT Shop Wishlist Overlay - install only %date% %time% =====

rem This script does NOT build. It deploys the DLL already sitting in bin\Release\net472
rem (built via MSBuild /t:Build - see CLAUDE.md) to the HDT
rem Plugins folder, then restarts Hearthstone Deck Tracker.

set "DLL="
for /f "delims=" %%F in ('dir /b /s "%ROOT%bin\Release\net472\HDT-Shop-Wishlist-Overlay.dll" 2^>nul') do if not defined DLL set "DLL=%%F"
if not defined DLL for /f "delims=" %%F in ('dir /b /s "%ROOT%bin\Release\HDT-Shop-Wishlist-Overlay.dll" 2^>nul') do if not defined DLL set "DLL=%%F"
if not defined DLL (
  echo ERROR: No built DLL found under %ROOT%bin\Release. Build first ^(MSBuild /t:Build^).
  echo ERROR: No built DLL found.>>"%LOG%"
  goto :fail
)
echo Found DLL: %DLL%
echo Found DLL: %DLL%>>"%LOG%"

set "HDT_ROOT=%LOCALAPPDATA%\HearthstoneDeckTracker"
set "HDT_EXE="
for /f "delims=" %%D in ('dir /b /ad /o-n "%HDT_ROOT%\app-*" 2^>nul') do (
  if not defined HDT_EXE if exist "%HDT_ROOT%\%%D\HearthstoneDeckTracker.exe" set "HDT_EXE=%HDT_ROOT%\%%D\HearthstoneDeckTracker.exe"
)
if not defined HDT_EXE (
  echo ERROR: Could not find an installed HearthstoneDeckTracker.exe under %HDT_ROOT%.
  echo ERROR: HDT_EXE not found.>>"%LOG%"
  goto :fail
)
echo Detected HDT: %HDT_EXE%
echo Detected HDT: %HDT_EXE%>>"%LOG%"

echo Closing HDT if running...
rem A plain (non-elevated) taskkill cannot close a previously-elevated HDT instance ("Access
rem denied") - it fails silently, and the next elevated launch just piles a new instance on
rem top instead of replacing it. Clean up any leftovers from earlier runs first.
del "%TEMP%\hdt_elevate_*.vbs" >nul 2>&1
del "%TEMP%\hdt_elevate_*.cmd" >nul 2>&1
taskkill /IM HearthstoneDeckTracker.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

set "PLUGIN_DIR=%APPDATA%\HearthstoneDeckTracker\Plugins"
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%" >nul 2>&1

echo Installing plugin to: %PLUGIN_DIR%
echo Installing plugin to: %PLUGIN_DIR%>>"%LOG%"
copy /y "%DLL%" "%PLUGIN_DIR%\HDT-Shop-Wishlist-Overlay.dll" >>"%LOG%" 2>&1 || goto :fail

if exist "%ROOT%Assets\BGCompBuilderIcon.png" (
  if not exist "%PLUGIN_DIR%\Assets" mkdir "%PLUGIN_DIR%\Assets" >nul 2>&1
  copy /y "%ROOT%Assets\BGCompBuilderIcon.png" "%PLUGIN_DIR%\Assets\BGCompBuilderIcon.png" >>"%LOG%" 2>&1 || goto :fail
)
rem xcopy only adds and overwrites, it never removes. A frame set that shrinks or gets renamed
rem would otherwise leave stale frames behind, and the plugin picks up every frame_*.png it
rem finds - so the animation would silently run on a mix of old and new art. Clear the
rem destination first, but only when the source actually has a replacement for it.
if exist "%ROOT%Assets\TribeIcons" (
  if exist "%PLUGIN_DIR%\Assets\TribeIcons" rmdir /s /q "%PLUGIN_DIR%\Assets\TribeIcons" >nul 2>&1
  mkdir "%PLUGIN_DIR%\Assets\TribeIcons" >nul 2>&1
  xcopy /e /i /y "%ROOT%Assets\TribeIcons" "%PLUGIN_DIR%\Assets\TribeIcons" >>"%LOG%" 2>&1 || goto :fail
)
if exist "%ROOT%Assets\RarityGlow" (
  if exist "%PLUGIN_DIR%\Assets\RarityGlow" rmdir /s /q "%PLUGIN_DIR%\Assets\RarityGlow" >nul 2>&1
  mkdir "%PLUGIN_DIR%\Assets\RarityGlow" >nul 2>&1
  xcopy /e /i /y "%ROOT%Assets\RarityGlow" "%PLUGIN_DIR%\Assets\RarityGlow" >>"%LOG%" 2>&1 || goto :fail
)

set "DLL_DIR="
for %%F in ("%DLL%") do set "DLL_DIR=%%~dpF"
if exist "%DLL_DIR%untapped-scry-dotnet.dll" copy /y "%DLL_DIR%untapped-scry-dotnet.dll" "%PLUGIN_DIR%\untapped-scry-dotnet.dll" >>"%LOG%" 2>&1

if not exist "%PLUGIN_DIR%\HDT-Shop-Wishlist-Overlay.dll" (
  echo ERROR: Plugin copy verification failed.
  echo ERROR: Plugin copy verification failed.>>"%LOG%"
  goto :fail
)

echo Restarting Hearthstone Deck Tracker (elevated - a Windows admin prompt WILL appear, click Yes)...
rem Launched elevated so the in-game "Skip Combat" button can add/remove a temporary Windows
rem Firewall rule (blocking Hearthstone.exe's network traffic for a few seconds) without a UAC
rem prompt interrupting the match every time the button is clicked.
rem One elevated helper .cmd does an elevated taskkill (catches previously-elevated instances
rem the plain taskkill above could not touch) THEN launches HDT, so only a single UAC prompt is
rem needed. Launched via Shell.Application ShellExecute "runas" (same mechanism as right-click
rem > "Run as administrator" in Explorer) instead of PowerShell's Start-Process -Verb RunAs,
rem which does not always reliably surface the consent dialog from a non-interactive parent.
set "ELEV_CMD=%TEMP%\hdt_elevate_%RANDOM%.cmd"
> "%ELEV_CMD%" echo @echo off
>>"%ELEV_CMD%" echo taskkill /F /IM HearthstoneDeckTracker.exe ^>nul 2^>^&1
>>"%ELEV_CMD%" echo timeout /t 1 /nobreak ^>nul
>>"%ELEV_CMD%" echo start "" "%HDT_EXE%"

set "ELEV_VBS=%TEMP%\hdt_elevate_%RANDOM%.vbs"
> "%ELEV_VBS%" echo Set UAC = CreateObject^("Shell.Application"^)
>>"%ELEV_VBS%" echo UAC.ShellExecute "%ELEV_CMD%", "", "", "runas", 1
cscript //nologo "%ELEV_VBS%" >>"%LOG%" 2>&1
del "%ELEV_VBS%" >nul 2>&1
rem Not deleting %ELEV_CMD% here: ShellExecute is fire-and-forget, and the elevated cmd may
rem still be waiting on the UAC prompt / mid-run. It gets cleaned up at the top of the next run.

echo.
echo SUCCESS: plugin installed and HDT restarted.
echo SUCCESS: plugin installed and HDT restarted.>>"%LOG%"
echo Install log: %LOG%
pause
exit /b 0

:fail
echo.
echo INSTALL FAILED. See %LOG%
echo INSTALL FAILED.>>"%LOG%"
echo Install log: %LOG%
pause
exit /b 1
