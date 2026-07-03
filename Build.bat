@echo off
chcp 65001 >nul
echo ========================================
echo   ApiTester - Build Script
echo ========================================
echo.

dotnet build -c Release %*

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed!
    pause
    exit /b 1
)

echo.
echo [OK] Build succeeded!
echo Output: bin\Release\net10.0-windows\
echo.
pause
