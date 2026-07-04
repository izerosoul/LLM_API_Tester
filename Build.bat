@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild not found: %MSBUILD%
    exit /b 1
)

set "TFM_ROOT="
set "NET481=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1"
if not exist "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8" (
    if not exist "%NET481%" (
        echo [ERROR] .NET Framework 4.8 reference assemblies not found.
        echo Install the .NET Framework 4.8 Developer Pack and retry.
        exit /b 1
    )
    set "TFM_ROOT=%CD%\.build\refs"
    if not exist "%CD%\.build\refs\.NETFramework" mkdir "%CD%\.build\refs\.NETFramework"
    if not exist "%CD%\.build\refs\.NETFramework\v4.8" (
        mklink /J "%CD%\.build\refs\.NETFramework\v4.8" "%NET481%" >nul
        if errorlevel 1 (
            echo [ERROR] Failed to create local .NET Framework reference fallback.
            exit /b 1
        )
    )
)

if not exist "%CD%\.build\offline-packages" mkdir "%CD%\.build\offline-packages"

echo ========================================
echo   ApiTester - Release Build
echo   Target: .NET Framework 4.8
echo ========================================
echo.

if "%TFM_ROOT%"=="" (
    "%MSBUILD%" ApiTester.csproj /t:Build /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x64 /p:ExcludeRestorePackageImports=true /p:ResolveNuGetPackages=false /m
) else (
    "%MSBUILD%" ApiTester.csproj /t:Build /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x64 /p:TargetFrameworkRootPath=%TFM_ROOT% /p:ExcludeRestorePackageImports=true /p:ResolveNuGetPackages=false /m
)
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed!
    exit /b 1
)

echo.
echo [OK] Build succeeded!
echo Output: bin\Release\
endlocal
