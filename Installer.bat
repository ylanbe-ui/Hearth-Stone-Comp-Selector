@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

echo ============================================
echo   HDT Shop Wishlist Overlay - Installation
echo ============================================
echo.

if not exist "HDT-Shop-Wishlist-Overlay.dll" (
  echo ERREUR: HDT-Shop-Wishlist-Overlay.dll est introuvable a cote de ce script.
  echo Assure-toi d'avoir garde tous les fichiers ensemble dans le meme dossier.
  pause
  exit /b 1
)

set "PLUGIN_DIR=%APPDATA%\HearthstoneDeckTracker\Plugins"
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%" >nul 2>&1

echo Fermeture de Hearthstone Deck Tracker s'il est ouvert...
del "%TEMP%\hdt_elevate_*.vbs" >nul 2>&1
taskkill /IM HearthstoneDeckTracker.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

echo Copie du plugin vers : %PLUGIN_DIR%
copy /y "HDT-Shop-Wishlist-Overlay.dll" "%PLUGIN_DIR%\HDT-Shop-Wishlist-Overlay.dll" >nul || goto :fail
copy /y "untapped-scry-dotnet.dll" "%PLUGIN_DIR%\untapped-scry-dotnet.dll" >nul || goto :fail

rem The icon files are cosmetic only (missing ones just show as blank/fallback text in the
rem overlay, nothing breaks). Some file-transfer tools flatten subfolders when sharing a
rem folder, so look for each file both under Assets\... and directly next to this script,
rem and just skip with a warning instead of failing the whole install if neither is found.
if not exist "%PLUGIN_DIR%\Assets" mkdir "%PLUGIN_DIR%\Assets" >nul 2>&1
if not exist "%PLUGIN_DIR%\Assets\TribeIcons" mkdir "%PLUGIN_DIR%\Assets\TribeIcons" >nul 2>&1

if exist "Assets\BGCompBuilderIcon.png" (
  copy /y "Assets\BGCompBuilderIcon.png" "%PLUGIN_DIR%\Assets\BGCompBuilderIcon.png" >nul
) else if exist "BGCompBuilderIcon.png" (
  copy /y "BGCompBuilderIcon.png" "%PLUGIN_DIR%\Assets\BGCompBuilderIcon.png" >nul
) else (
  echo AVERTISSEMENT: BGCompBuilderIcon.png introuvable, ignore ^(cosmetique seulement^).
)

if exist "Assets\TribeIcons\*.png" (
  xcopy /e /i /y "Assets\TribeIcons\*.png" "%PLUGIN_DIR%\Assets\TribeIcons\" >nul
) else (
  set "FOUND_FLAT_ICON="
  for %%T in (Beast Demon Dragon Elemental Mech Murloc Naga Pirate Quilboar Undead) do (
    if exist "%%T.png" (
      set "FOUND_FLAT_ICON=1"
      copy /y "%%T.png" "%PLUGIN_DIR%\Assets\TribeIcons\%%T.png" >nul
    )
  )
  if not defined FOUND_FLAT_ICON echo AVERTISSEMENT: icones de tribu introuvables, ignorees ^(cosmetique seulement^).
)


rem Rarity-glow badge frames. The destination is cleared first: xcopy only adds and overwrites,
rem never removes, and the plugin animates every frame_*.png it finds - so a frame set that
rem shrinks between versions would silently animate a mix of old and new art.
if exist "Assets\RarityGlow" (
  if exist "%PLUGIN_DIR%\Assets\RarityGlow" rmdir /s /q "%PLUGIN_DIR%\Assets\RarityGlow" >nul 2>&1
  mkdir "%PLUGIN_DIR%\Assets\RarityGlow" >nul 2>&1
  xcopy /e /i /y "Assets\RarityGlow" "%PLUGIN_DIR%\Assets\RarityGlow" >nul
) else (
  echo AVERTISSEMENT: Assets\RarityGlow introuvable, badges d etoile ignores ^(cosmetique seulement^).
)

if not exist "%PLUGIN_DIR%\HDT-Shop-Wishlist-Overlay.dll" (
  echo ERREUR: la copie du plugin a echoue.
  goto :fail
)

echo.
echo Plugin installe avec succes !
echo.
echo Le rail (paliers de tavern), le surlignage de boutique et le builder de
echo comp fonctionnent tout de suite, sans rien de plus.
echo.
echo Hearthstone Deck Tracker se relance toujours en administrateur (une
echo fenetre Windows va demander confirmation - clique "Oui") : ca garde le
echo bouton "Skip Combat" du rail toujours disponible, et evite les
echo comportements incoherents entre lancements normaux et mises a jour
echo automatiques, qui relancent elles aussi en administrateur.
echo.

set "HDT_ROOT=%LOCALAPPDATA%\HearthstoneDeckTracker"
set "HDT_EXE="
for /f "delims=" %%D in ('dir /b /ad /o-n "%HDT_ROOT%\app-*" 2^>nul') do (
  if not defined HDT_EXE if exist "%HDT_ROOT%\%%D\HearthstoneDeckTracker.exe" set "HDT_EXE=%HDT_ROOT%\%%D\HearthstoneDeckTracker.exe"
)

echo Relancer Hearthstone Deck Tracker maintenant ?
echo   [n] Non
echo   [o] Oui, en administrateur
set /p LAUNCH="Ton choix (n/o) : "

if /i "%LAUNCH%"=="o" (
  if not defined HDT_EXE (
    echo Impossible de trouver Hearthstone Deck Tracker automatiquement. Relance-le toi-meme en admin.
    goto :done
  )
  echo Une fenetre Windows va demander confirmation admin - clique "Oui".
  set "ELEV_VBS=%TEMP%\hdt_elevate_%RANDOM%.vbs"
  > "!ELEV_VBS!" echo Set UAC = CreateObject^("Shell.Application"^)
  >>"!ELEV_VBS!" echo UAC.ShellExecute "%HDT_EXE%", "", "", "runas", 1
  cscript //nologo "!ELEV_VBS!" >nul 2>&1
  del "!ELEV_VBS!" >nul 2>&1
  goto :done
)

:done
echo.
pause
exit /b 0

:fail
echo.
echo INSTALLATION ECHOUEE.
pause
exit /b 1
