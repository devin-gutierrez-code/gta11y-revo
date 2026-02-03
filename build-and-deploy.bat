@echo off
setlocal enabledelayedexpansion

REM Clear previous build log
if exist "%~dp0build_results.txt" del "%~dp0build_results.txt"

echo ======================================== > "%~dp0build_results.txt"
echo GTA11Y Build and Deploy Script >> "%~dp0build_results.txt"
echo ======================================== >> "%~dp0build_results.txt"
echo. >> "%~dp0build_results.txt"

echo ========================================
echo GTA11Y Build and Deploy Script
echo ========================================
echo.
echo Build log will be saved to: build_results.txt
echo.

REM Try to find MSBuild
set MSBUILD=

REM Version number based (e.g., 18, 17, etc.)
if exist "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

REM Visual Studio 2026/2025 locations
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2025\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2025\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2025\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2025\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

REM Visual Studio 2022 locations
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

REM Visual Studio 2019 locations
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"

REM Build Tools standalone
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if "%MSBUILD%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

REM Check if we found it
if "%MSBUILD%"=="" (
    echo ERROR: Could not find MSBuild.exe automatically. >> "%~dp0build_results.txt"
    echo ERROR: Could not find MSBuild.exe automatically.
    echo.
    echo Please install Visual Studio 2019/2022 or Build Tools.
    pause
    exit /b 1
)

echo Found MSBuild: %MSBUILD% >> "%~dp0build_results.txt"
echo Found MSBuild: %MSBUILD%
echo. >> "%~dp0build_results.txt"
echo.

echo Step 1/3: Restoring NuGet packages... >> "%~dp0build_results.txt"
echo Step 1/3: Restoring NuGet packages...
echo. >> "%~dp0build_results.txt"
echo.

"%MSBUILD%" "%~dp0GTA.sln" /t:Restore >> "%~dp0build_results.txt" 2>&1

echo. >> "%~dp0build_results.txt"
echo Step 2/3: Building project... >> "%~dp0build_results.txt"
echo Step 2/3: Building project...
echo. >> "%~dp0build_results.txt"
echo.

"%MSBUILD%" "%~dp0GTA.sln" /p:Configuration=Release /p:Platform=x64 /t:Clean,Build /m >> "%~dp0build_results.txt" 2>&1

if errorlevel 1 (
    echo. >> "%~dp0build_results.txt"
    echo BUILD FAILED! See build_results.txt for details. >> "%~dp0build_results.txt"
    echo.
    echo ========================================
    echo BUILD FAILED!
    echo ========================================
    echo.
    echo Check build_results.txt for full error details.
    echo.
    pause
    exit /b 1
)

echo. >> "%~dp0build_results.txt"
echo BUILD SUCCESSFUL! >> "%~dp0build_results.txt"
echo.
echo BUILD SUCCESSFUL!
echo.
echo ======================================== >> "%~dp0build_results.txt"
echo Step 3/3: Deploying to GTA V... >> "%~dp0build_results.txt"
echo ======================================== >> "%~dp0build_results.txt"
echo ========================================
echo Step 3/3: Deploying to GTA V...
echo ========================================
echo.

REM Call the deployment script
call "%~dp0deploy.bat" >> "%~dp0build_results.txt" 2>&1

echo. >> "%~dp0build_results.txt"
echo Complete! Log saved to: build_results.txt >> "%~dp0build_results.txt"
