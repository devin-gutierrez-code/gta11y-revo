@echo off
echo ========================================
echo GTA11Y Build Script
echo ========================================
echo.

REM Find MSBuild - try common locations
set "MSBUILD_18=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "MSBUILD_2026=C:\Program Files\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "MSBUILD_2025=C:\Program Files\Microsoft Visual Studio\2025\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set "MSBUILD_2022=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
set "MSBUILD_2019=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"

if exist "%MSBUILD_18%" (
    set "MSBUILD=%MSBUILD_18%"
    echo Found MSBuild Version 18
) else if exist "%MSBUILD_2026%" (
    set "MSBUILD=%MSBUILD_2026%"
    echo Found MSBuild 2026 Build Tools
) else if exist "%MSBUILD_2025%" (
    set "MSBUILD=%MSBUILD_2025%"
    echo Found MSBuild 2025 Build Tools
) else if exist "%MSBUILD_2022%" (
    set "MSBUILD=%MSBUILD_2022%"
    echo Found MSBuild 2022
) else if exist "%MSBUILD_2019%" (
    set "MSBUILD=%MSBUILD_2019%"
    echo Found MSBuild 2019
) else (
    echo ERROR: Could not find MSBuild.exe
    echo Please ensure Visual Studio 2019 or 2022 is installed.
    pause
    exit /b 1
)

echo.
echo Building GTA11Y in Release x64 configuration...
echo.

"%MSBUILD%" "%~dp0GTA.sln" /p:Configuration=Release /p:Platform=x64 /t:Clean,Build /m /v:minimal

if errorlevel 1 (
    echo.
    echo ========================================
    echo BUILD FAILED!
    echo ========================================
    pause
    exit /b 1
) else (
    echo.
    echo ========================================
    echo BUILD SUCCESSFUL!
    echo ========================================
    echo.
    echo Output: GTA\obj\x64\Release\GrandTheftAccessibility.dll
    echo.
)

pause
