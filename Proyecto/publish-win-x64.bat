@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================
echo   dvdrtool - Publicar Windows x64
echo ============================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: No se encontro dotnet.
    echo Instala el SDK de .NET 8:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

set "HAS_NET8="
for /f "tokens=1" %%V in ('dotnet --list-sdks 2^>nul ^| findstr /r /b "8\.0\."') do set "HAS_NET8=1"

if not defined HAS_NET8 (
    echo ERROR: Falta el SDK .NET 8.
    echo.
    dotnet --list-sdks
    echo.
    echo Instala el SDK .NET 8:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo SDK seleccionado:
dotnet --version
echo.

dotnet publish dvdrtool.csproj -c Release -r win-x64 --self-contained true --nologo
if errorlevel 1 goto :error

echo.
echo ============================================
echo   PUBLICACION COMPLETADA
echo ============================================
echo.
echo Salida:
echo %CD%\bin\Release\net8.0-windows\win-x64\publish\
echo.
pause
exit /b 0

:error
echo.
echo ERROR al publicar.
echo SDK usado:
dotnet --version
echo.
pause
exit /b 1
