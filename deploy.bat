@echo off
setlocal enabledelayedexpansion

echo ========================================
echo GTA11Y Deployment Script
echo ========================================
echo.

REM Set source and destination paths
set "SOURCE_DIR=%~dp0"
set "BUILD_DIR=%SOURCE_DIR%GTA\bin\x64\Release"
set "RESOURCES_DIR=%SOURCE_DIR%External Resources"
set "GAME_DIR=C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V"
set "SCRIPTS_DIR=%GAME_DIR%\scripts"

REM Check if game directory exists
if not exist "%GAME_DIR%" (
    echo ERROR: GTA V installation not found at:
    echo "%GAME_DIR%"
    echo.
    echo Please verify your GTA V installation path.
    pause
    exit /b 1
)

REM Create scripts directory if it doesn't exist
if not exist "%SCRIPTS_DIR%" (
    echo Creating scripts directory...
    mkdir "%SCRIPTS_DIR%"
    if errorlevel 1 (
        echo ERROR: Failed to create scripts directory.
        echo You may need to run this script as Administrator.
        pause
        exit /b 1
    )
)

echo Source Directory: "%SOURCE_DIR%"
echo Build Directory: "%BUILD_DIR%"
echo Game Directory: "%GAME_DIR%"
echo Scripts Directory: "%SCRIPTS_DIR%"
echo.

REM Check if build files exist
if not exist "%BUILD_DIR%\GrandTheftAccessibility.dll" (
    echo ERROR: Build files not found!
    echo Please build the project in Release x64 configuration first.
    pause
    exit /b 1
)

echo Copying main DLL...
copy /Y "%BUILD_DIR%\GrandTheftAccessibility.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy GrandTheftAccessibility.dll
    echo You may need to run this script as Administrator.
    pause
    exit /b 1
)
echo - GrandTheftAccessibility.dll copied

echo.
echo Copying dependency DLLs...
copy /Y "%SOURCE_DIR%packages\NAudio.1.10.0\lib\net35\NAudio.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo WARNING: Failed to copy NAudio.dll
) else (
    echo - NAudio.dll copied
)

copy /Y "%SOURCE_DIR%packages\CSCore.1.2.1.2\lib\net35-client\CSCore.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo WARNING: Failed to copy CSCore.dll
) else (
    echo - CSCore.dll copied
)

copy /Y "%SOURCE_DIR%packages\Newtonsoft.Json.12.0.3\lib\net45\Newtonsoft.Json.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo WARNING: Failed to copy Newtonsoft.Json.dll
) else (
    echo - Newtonsoft.Json.dll copied
)

echo.
echo Copying Tolk libraries...
copy /Y "%SOURCE_DIR%lib\TolkDotNet.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo WARNING: Failed to copy TolkDotNet.dll
) else (
    echo - TolkDotNet.dll copied
)

copy /Y "%SOURCE_DIR%lib\Tolk.dll" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo WARNING: Failed to copy Tolk.dll
) else (
    echo - Tolk.dll copied
)

echo.
echo Copying audio files...
copy /Y "%RESOURCES_DIR%\tped.wav" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy tped.wav
    pause
    exit /b 1
)
echo - tped.wav copied

copy /Y "%RESOURCES_DIR%\tvehicle.wav" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy tvehicle.wav
    pause
    exit /b 1
)
echo - tvehicle.wav copied

copy /Y "%RESOURCES_DIR%\tprop.wav" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy tprop.wav
    pause
    exit /b 1
)
echo - tprop.wav copied

echo.
echo Copying hashes database...
copy /Y "%RESOURCES_DIR%\hashes.txt" "%SCRIPTS_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy hashes.txt
    pause
    exit /b 1
)
echo - hashes.txt copied

echo.
echo ========================================
echo Deployment completed successfully!
echo ========================================
echo.
echo Launching GTA V via Steam...
echo.

REM Launch GTA V through Steam
start steam://rungameid/271590

echo Game launch initiated.
echo You can close this window.
echo.
pause
