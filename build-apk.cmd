@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

if "%~1"=="--internal-log" goto :run_logged
if exist build-apk.log del /f /q build-apk.log
powershell -NoProfile -Command "& { & '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build-apk.log; $code = $LASTEXITCODE; if (Test-Path build-apk.log) { (Get-Content -Path build-apk.log) | Set-Content -Path build-apk.log -Encoding utf8 }; exit $code }"
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - APK was not produced.
  echo ###########################################################
)
if "%RC%"=="2" (
  echo.
  echo ###########################################################
  echo  BUILD OK - but the release was NOT updated.
  echo  .\compiled\RDPVault.apk is good and usable.
  echo ###########################################################
)

popd >nul
exit /b %RC%

:run_logged
shift
setlocal enabledelayedexpansion
cd /d "."

set "DO_PUBLISH=0"
if /I "%~1"=="--publish" set "DO_PUBLISH=1"

set "PROJECT_FILE=RDPVault.Android\RDPVault.Android.csproj"
set "OUTPUT_APK=RDPVault.apk"
set "OUTPUT_DIR=.\compiled"
set "FINAL_DIR=.\obj\Android_final"

echo ###########################################################
echo CHECKING ANDROID BUILD PREREQUISITES...
echo ###########################################################
dotnet workload list | findstr /i "android" >nul 2>&1
if errorlevel 1 (
  echo [WARNING] .NET Android workload is not yet installed.
  echo Run: dotnet workload install android
  echo to enable local compilation of the Android APK.
  exit /b 1
)

echo ###########################################################
echo PURGING PREVIOUS ANDROID ARTIFACTS...
echo ###########################################################
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

echo.
echo ###########################################################
echo BUILDING RDP Vault: Android APK Release (ARM64 / x86_64)
echo ###########################################################
dotnet publish "%PROJECT_FILE%" -c Release -p:AndroidPackageFormat=apk -p:TreatWarningsAsErrors=true -o "%FINAL_DIR%" -consoleLoggerParameters:Summary
if errorlevel 1 exit /b 1

for /r "%FINAL_DIR%" %%F in (*-Signed.apk *.apk) do (
  if not defined FOUND_APK (
    set "FOUND_APK=%%F"
  )
)

if not defined FOUND_APK (
  echo ERROR: Expected output APK was not produced.
  exit /b 1
)

copy /y "!FOUND_APK!" "%OUTPUT_DIR%\%OUTPUT_APK%" >nul
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Android APK build completed successfully.
echo.
echo Single APK: %OUTPUT_DIR%\%OUTPUT_APK%
echo Log file:   .\build-apk.log
echo ###########################################################

if "!DO_PUBLISH!"=="0" (
  echo.
  echo [PUBLISH] Skipped on request. Pass --publish to upload asset.
  exit /b 0
)

exit /b 0
