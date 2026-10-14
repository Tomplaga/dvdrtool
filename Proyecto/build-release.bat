@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================
echo   dvdrtool - Compilar Release con .NET 8
echo ============================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: No se encontro dotnet.
    echo Instala el SDK de .NET 8 y vuelve a ejecutar este archivo.
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

set "HAS_NET8="
for /f "tokens=1" %%V in ('dotnet --list-sdks 2^>nul ^| findstr /r /b "8\.0\."') do set "HAS_NET8=1"

if not defined HAS_NET8 (
    echo ERROR: No hay ningun SDK .NET 8 instalado.
    echo.
    echo SDK detectados:
    dotnet --list-sdks
    echo.
    echo dvdrtool usa .NET 8. El SDK 10 instalado en tu PC no se usara
    echo porque en tu version de Windows esta provocando el error CET.
    echo.
    echo Instala "SDK .NET 8" desde:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo SDK seleccionado por global.json:
dotnet --version
echo.

dotnet build dvdrtool.csproj -c Release --nologo
if errorlevel 1 goto :error

echo.
echo ============================================
echo   COMPILACION COMPLETADA
echo ============================================
echo.
echo Salida:
echo %CD%\bin\Release\net8.0-windows\
echo.
pause
exit /b 0

:error
echo.
echo ============================================
echo   ERROR AL COMPILAR
echo ============================================
echo.
echo SDK usado:
dotnet --version
echo.
pause
exit /b 1
