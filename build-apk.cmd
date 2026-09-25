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

if not defined ANDROID_HOME (
  if exist "%LOCALAPPDATA%\RdpVaultBuildTools\android-sdk" set "ANDROID_HOME=%LOCALAPPDATA%\RdpVaultBuildTools\android-sdk"
  if exist "%LOCALAPPDATA%\Android\Sdk" set "ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk"
)
if not defined ANDROID_SDK_ROOT if defined ANDROID_HOME set "ANDROID_SDK_ROOT=!ANDROID_HOME!"

if not defined JAVA_HOME (
  if exist "%LOCALAPPDATA%\RdpVaultBuildTools\jdk" set "JAVA_HOME=%LOCALAPPDATA%\RdpVaultBuildTools\jdk"
  if exist "C:\Program Files\Microsoft\jdk-17" set "JAVA_HOME=C:\Program Files\Microsoft\jdk-17"
)

set "SDK_PARAM="
if defined ANDROID_HOME set "SDK_PARAM=-p:AndroidSdkDirectory="!ANDROID_HOME!""
if defined JAVA_HOME set "SDK_PARAM=!SDK_PARAM! -p:JavaSdkDirectory="!JAVA_HOME!""

echo.
echo ###########################################################
echo BUILDING RDP Vault: Android APK Release (ARM64 / x86_64)
echo ###########################################################
dotnet publish "%PROJECT_FILE%" -c Release -p:AndroidPackageFormat=apk !SDK_PARAM! -o "%FINAL_DIR%" -consoleLoggerParameters:Summary
if errorlevel 1 exit /b 1

for /r "%FINAL_DIR%" %%F in (*-Signed.apk) do (
  if not defined FOUND_APK set "FOUND_APK=%%F"
)
if not defined FOUND_APK (
  for /r "%FINAL_DIR%" %%F in (*.apk) do (
    if not defined FOUND_APK set "FOUND_APK=%%F"
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

echo.
echo ###########################################################
echo PUBLISHING ANDROID RELEASE TO GITHUB...
echo ###########################################################
where gh >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: GitHub CLI ^(gh^) is not installed.
  exit /b 1
)
gh auth status >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: gh is not signed in.
  exit /b 1
)

set "REPO=alonreich/RDP-Encrypt"
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd"`) do set "TAG=v%%D"

echo [PUBLISH] Uploading %OUTPUT_DIR%\%OUTPUT_APK% to GitHub release !TAG!...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\UploadAsset.ps1" -Repo "!REPO!" -Tag "!TAG!" -FilePath "%OUTPUT_DIR%\%OUTPUT_APK%"
if errorlevel 1 (
  echo [PUBLISH] STOPPED: uploading release asset failed.
  exit /b 1
)

echo.
echo ###########################################################
echo SUCCESS: Android APK !TAG! is published to GitHub.
echo Download: https://github.com/!REPO!/releases/latest/download/%OUTPUT_APK%
echo ###########################################################
exit /b 0
