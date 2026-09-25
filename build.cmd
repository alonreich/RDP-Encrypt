@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

if "%~1"=="--internal-log" goto :run_logged
if exist build.log del /f /q build.log

powershell -NoProfile -Command ^
  "$sw = [System.Diagnostics.Stopwatch]::StartNew();" ^
  "& '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build.log;" ^
  "$code = $LASTEXITCODE;" ^
  "$sw.Stop();" ^
  "if (Test-Path build.log) {" ^
  "  $lines = Get-Content -Path build.log;" ^
  "  $warnCount = ($lines | Select-String -Pattern '(?i)\b(warning CS|warning XA|warning\b|\d+ Warning\(s\))' -AllMatches).Matches.Count;" ^
  "  $duration = '{0}m{1:D2}s' -f [int]$sw.Elapsed.TotalMinutes, $sw.Elapsed.Seconds;" ^
  "  $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss');" ^
  "  $label = if ($code -ne 0) { 'FAIL' } elseif ($warnCount -gt 0) { 'WARN' } else { 'OK' };" ^
  "  $detail = if ($code -ne 0) { \"- exit code $code\" } elseif ($warnCount -gt 0) { \"- $warnCount warning line(s)\" } else { '- clean success' };" ^
  "  $firstLine = \"$label $detail - RDP Vault build $timestamp ($duration)\";" ^
  "  $body = $lines -join [Environment]::NewLine;" ^
  "  Set-Content -Path build.log -Value ($firstLine + [Environment]::NewLine + $body) -Encoding utf8;" ^
  "};" ^
  "exit $code"
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - nothing was published.
  echo  The existing GitHub release was NOT touched.
  echo ###########################################################
)
if "%RC%"=="2" (
  echo.
  echo ###########################################################
  echo  BUILD OK - but the release was NOT updated.
  echo  .\compiled\RDPVault.exe is good and usable.
  echo ###########################################################
)

popd >nul

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\SetConsoleFont.ps1" <nul 2>nul

exit /b %RC%

:run_logged
shift
setlocal enabledelayedexpansion
cd /d "."

set "DO_PUBLISH=1"
if /I "%~1"=="--no-publish" set "DO_PUBLISH=0"

set "PROJECT_FILE=RDPVault\RDPVault.csproj"
set "PROJECT_EXE=RDPVault.exe"
set "OUTPUT_EXE=RDPVault.exe"
set "OUTPUT_DIR=.\compiled"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=true"
rem Self-contained single file. This is NOT a NativeAOT build - see
rem project_structure.txt SECTION 2. The csproj sets PublishAot=false on purpose.
set "PUBLISH_SF_ARGS=-p:PublishSingleFile=true -p:SelfContained=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:Summary"
set "ANDROID_BUILD_SUCCESS=0"
set "ANDROID_BUILD_ATTEMPTED=0"

for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd"`) do set "BUILD_VERSION=%%D"
set "TAG=v!BUILD_VERSION!"

echo ###########################################################
echo SYNCHRONIZING PROJECT VERSIONS: !BUILD_VERSION! ^(tag !TAG!^)
echo ###########################################################
echo !BUILD_VERSION!> "%SCRIPT_DIR%version.txt"

echo.
echo ###########################################################
echo PURGING PREVIOUS BUILD ARTIFACTS...
echo ###########################################################
call :TERMINATE_PROCESSES
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
if exist "%OUTPUT_DIR%\%OUTPUT_EXE%" del /f /q "%OUTPUT_DIR%\%OUTPUT_EXE%" 2>nul
call :CLEAN_ALL

echo.
echo ###########################################################
echo BUILDING RDP Vault: Self-Contained SingleFile win-x64
echo ###########################################################
call :BUILD_SINGLEFILE
if errorlevel 1 exit /b 1

if exist "%LOCALAPPDATA%\RdpVaultBuildTools\android-sdk" (
  echo.
  echo ###########################################################
  echo BUILDING RDP Vault: Android APK
  echo ###########################################################
  call :BUILD_ANDROID
  if errorlevel 1 exit /b 1
)

call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo.
echo Single EXE: %OUTPUT_DIR%\%OUTPUT_EXE%
if "!ANDROID_BUILD_SUCCESS!"=="1" (
  echo Android APK: %OUTPUT_DIR%\RDPVault.apk
)
echo Log file:   .\build.log  ^(first line: OK / WARN / FAIL verdict^)
echo ###########################################################

if "!DO_PUBLISH!"=="0" (
  echo.
  echo [PUBLISH] Skipped on request ^(--no-publish^).
  exit /b 0
)

echo.
echo ###########################################################
echo PUBLISHING RELEASE TO GITHUB...
echo ###########################################################
call :PUBLISH_RELEASE
if errorlevel 1 exit /b 2

exit /b 0

:PUBLISH_RELEASE
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

set "DYNAMIC_REPO="
for /f "usebackq delims=" %%R in (`gh repo view --json nameWithOwner --jq .nameWithOwner 2^>nul`) do set "DYNAMIC_REPO=%%R"
if defined DYNAMIC_REPO (
  set "REPO=!DYNAMIC_REPO!"
) else (
  set "REPO=alonreich/RDP-Encrypt"
)
echo [PUBLISH] 1/6 Target repository: !REPO!

set "LOCALHASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%OUTPUT_DIR%\%OUTPUT_EXE%" SHA256') do (
  if not defined LOCALHASH set "LOCALHASH=%%H"
)
set "LOCALHASH=!LOCALHASH: =!"
if not defined LOCALHASH (
  echo [PUBLISH] STOPPED: could not fingerprint the freshly built exe.
  exit /b 1
)
echo [PUBLISH] 2/6 Built exe fingerprint ready ^(!LOCALHASH!^).

echo [PUBLISH] 3/6 Purging previous releases and remote/local git tags...
set "REMOVED=0"
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do (
  gh release delete %%T --repo !REPO! --cleanup-tag --yes >nul 2>&1 || gh release delete %%T --repo !REPO! --yes >nul 2>&1
  set /a REMOVED+=1
)
git fetch --tags --prune --prune-tags >nul 2>&1
for /f "usebackq delims=" %%L in (`git tag --list 2^>nul`) do (
  git push origin --delete %%L >nul 2>&1
  git tag -d %%L >nul 2>&1
)

set "SURVIVORS=0"
for /f "usebackq delims=" %%S in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do set /a SURVIVORS+=1
if not "!SURVIVORS!"=="0" (
  echo [PUBLISH] STOPPED: !SURVIVORS! previous release^(s^) could not be removed.
  exit /b 1
)
echo [PUBLISH] Removed !REMOVED! previous release^(s^) and purged all tags.

echo [PUBLISH] 4/6 Uploading %OUTPUT_DIR%\%OUTPUT_EXE% to GitHub release !TAG!...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\UploadAsset.ps1" -Repo "!REPO!" -Tag "!TAG!" -FilePath "%OUTPUT_DIR%\%OUTPUT_EXE%" -LocalHash "!LOCALHASH!"
if errorlevel 1 (
  echo [PUBLISH] STOPPED: uploading release asset failed.
  exit /b 1
)
if "!ANDROID_BUILD_SUCCESS!"=="1" if exist "%OUTPUT_DIR%\RDPVault.apk" (
  echo [PUBLISH] 5/6 Uploading %OUTPUT_DIR%\RDPVault.apk to GitHub release !TAG!...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\UploadAsset.ps1" -Repo "!REPO!" -Tag "!TAG!" -FilePath "%OUTPUT_DIR%\RDPVault.apk"
  if errorlevel 1 (
    echo [PUBLISH] STOPPED: uploading Android APK failed.
    exit /b 1
  )
)

echo [PUBLISH] 6/6 Verifying published assets on GitHub...
set "EXE_VERIFIED=0"
set "APK_VERIFIED=0"
for /f "usebackq delims=" %%A in (`gh release view !TAG! --repo !REPO! --json assets --jq ".assets[].name" 2^>nul`) do (
  if /I "%%A"=="%OUTPUT_EXE%" set "EXE_VERIFIED=1"
  if /I "%%A"=="RDPVault.apk" set "APK_VERIFIED=1"
)
if "!EXE_VERIFIED!"=="0" (
  echo [PUBLISH] STOPPED: %OUTPUT_EXE% was not found on remote release !TAG!.
  exit /b 1
)
if "!ANDROID_BUILD_SUCCESS!"=="1" if "!APK_VERIFIED!"=="0" (
  echo [PUBLISH] STOPPED: RDPVault.apk was not found on remote release !TAG!.
  exit /b 1
)

set "RELEASECOUNT=0"
for /f "usebackq delims=" %%R in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do set /a RELEASECOUNT+=1
if not "!RELEASECOUNT!"=="1" (
  echo [PUBLISH] WARNING: !RELEASECOUNT! releases exist; expected exactly one.
)

echo.
echo ###########################################################
echo SUCCESS: release !TAG! is live and verified on GitHub.
echo Windows EXE: https://github.com/!REPO!/releases/latest/download/%OUTPUT_EXE%
if "!ANDROID_BUILD_SUCCESS!"=="1" (
  echo Android APK: https://github.com/!REPO!/releases/latest/download/RDPVault.apk
)
echo SHA256:      !LOCALHASH!
echo ###########################################################
exit /b 0

:BUILD_SINGLEFILE
set "FINAL_DIR=.\obj\SingleFile_final"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"

dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% %PUBLISH_SF_ARGS% -o "%FINAL_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

if not exist "%FINAL_DIR%\%PROJECT_EXE%" (
  echo ERROR: Expected single-file EXE was not produced.
  exit /b 1
)

move /y "%FINAL_DIR%\%PROJECT_EXE%" "%OUTPUT_DIR%\%OUTPUT_EXE%" >nul
if errorlevel 1 exit /b 1

call :PURGE_COMPILED_EXTRAS
if errorlevel 1 exit /b 1
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
exit /b 0

:BUILD_ANDROID
set "ANDROID_BUILD_ATTEMPTED=1"
set "ANDROID_BUILD_SUCCESS=0"
set "ANDROID_SDK=%LOCALAPPDATA%\RdpVaultBuildTools\android-sdk"
set "JAVA_SDK=%LOCALAPPDATA%\RdpVaultBuildTools\jdk"

if exist "%OUTPUT_DIR%\RDPVault.apk" del /f /q "%OUTPUT_DIR%\RDPVault.apk" 2>nul
if exist "RDPVault.Android\bin\Release\net9.0-android\com.rdpvault.app-Signed.apk" del /f /q "RDPVault.Android\bin\Release\net9.0-android\com.rdpvault.app-Signed.apk" 2>nul

dotnet build RDPVault.Android\RDPVault.Android.csproj -c Release /p:AndroidSdkDirectory="%ANDROID_SDK%" /p:JavaSdkDirectory="%JAVA_SDK%" %DOTNET_LOG_ARGS%
if errorlevel 1 (
  echo [ERROR] Android APK build failed.
  exit /b 1
)
if not exist "RDPVault.Android\bin\Release\net9.0-android\com.rdpvault.app-Signed.apk" (
  echo [ERROR] Expected Android signed APK was not produced.
  exit /b 1
)

copy /y "RDPVault.Android\bin\Release\net9.0-android\com.rdpvault.app-Signed.apk" "%OUTPUT_DIR%\RDPVault.apk" >nul
if errorlevel 1 (
  echo [ERROR] Failed to copy Android signed APK to compiled output directory.
  exit /b 1
)
set "ANDROID_BUILD_SUCCESS=1"
echo [ANDROID] Freshly signed APK copied to %OUTPUT_DIR%\RDPVault.apk.
exit /b 0

:PURGE_COMPILED_EXTRAS
for /d %%D in ("%OUTPUT_DIR%\*") do rd /s /q "%%~fD" 2>nul
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" if /I not "%%~nxF"=="RDPVault.apk" (
    del /f /q "%%~fF" 2>nul
  )
)
exit /b 0

:VALIDATE_COMPILED_OUTPUT
if not exist "%OUTPUT_DIR%\%OUTPUT_EXE%" exit /b 1
set "EXTRA=0"
for /d %%D in ("%OUTPUT_DIR%\*") do set /a EXTRA+=1
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" if /I not "%%~nxF"=="RDPVault.apk" set /a EXTRA+=1
)
if not "!EXTRA!"=="0" (
  echo ERROR: %OUTPUT_DIR% must contain only %OUTPUT_EXE% and RDPVault.apk; found !EXTRA! extra item^(s^).
  exit /b 1
)
exit /b 0

:TERMINATE_PROCESSES
taskkill /F /IM RDPVault.exe /T 2>nul
dotnet build-server shutdown 2>nul
exit /b 0

:CLEAN_ALL
if exist "RDPVault\bin" rd /s /q "RDPVault\bin" 2>nul
if exist "RDPVault\obj" rd /s /q "RDPVault\obj" 2>nul
if exist "RDPVault.Android\bin" rd /s /q "RDPVault.Android\bin" 2>nul
if exist "RDPVault.Android\obj" rd /s /q "RDPVault.Android\obj" 2>nul
if exist "%OUTPUT_DIR%\RDPVault.apk" del /f /q "%OUTPUT_DIR%\RDPVault.apk" 2>nul
dotnet clean RDPVault\RDPVault.csproj -c Release -r win-x64 --nologo -v q >nul 2>&1
exit /b 0
