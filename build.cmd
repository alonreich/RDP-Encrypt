@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

if "%~1"=="--internal-log" goto :run_logged
if exist build.log del /f /q build.log
powershell -NoProfile -Command "& { & '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build.log; exit $LASTEXITCODE }"
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - nothing was published.
  echo  The existing GitHub release was NOT touched.
  echo ###########################################################
  pause
)
if "%RC%"=="2" (
  echo.
  echo ###########################################################
  echo  BUILD OK - but the release was NOT updated.
  echo  .\compiled\RDPVault.exe is good and usable.
  echo ###########################################################
  pause
)
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
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=false"
rem Self-contained single file. This is NOT a NativeAOT build - see
rem project_structure.txt SECTION 2. The csproj sets PublishAot=false on purpose.
set "PUBLISH_SF_ARGS=-p:PublishSingleFile=true -p:SelfContained=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:ErrorsOnly"

echo ###########################################################
echo PURGING PREVIOUS BUILD ARTIFACTS...
echo ###########################################################
call :TERMINATE_PROCESSES
if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"
call :CLEAN_ALL

echo.
echo ###########################################################
echo BUILDING RDP Vault: Self-Contained SingleFile win-x64
echo ###########################################################
call :BUILD_SINGLEFILE
if errorlevel 1 exit /b 1

call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo.
echo Single EXE: %OUTPUT_DIR%\%OUTPUT_EXE%
echo Log file:   .\build.log
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

set "REPO=alonreich/RDP-Encrypt"

set "LOCALHASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%OUTPUT_DIR%\%OUTPUT_EXE%" SHA256') do (
  if not defined LOCALHASH set "LOCALHASH=%%H"
)
set "LOCALHASH=!LOCALHASH: =!"
if not defined LOCALHASH (
  echo [PUBLISH] STOPPED: could not fingerprint the freshly built exe.
  exit /b 1
)
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd"`) do set "TAG=v%%D"

rem Every previous release and tag is removed, so the repository always offers
rem exactly ONE download and the /releases/latest/download/ URL in README.md
rem can never resolve to a stale installer.
set "REMOVED=0"
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do (
  gh release delete %%T --repo !REPO! --cleanup-tag --yes >nul 2>&1
  set /a REMOVED+=1
)
echo [PUBLISH] Removed !REMOVED! previous release^(s^).

gh release create !TAG! "%OUTPUT_DIR%\%OUTPUT_EXE%" --repo !REPO! --title "RDP Vault !TAG!" --notes "Self-contained single-file win-x64 build published by build.cmd on !TAG!. This is the only supported download. SHA256 !LOCALHASH!" --latest >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: creating release !TAG! failed.
  exit /b 1
)

set "REMOTEHASH="
for /f "usebackq delims=" %%V in (`gh release view !TAG! --repo !REPO! --json assets --jq ".assets[0].digest" 2^>nul`) do set "REMOTEHASH=%%V"
set "REMOTEHASH=!REMOTEHASH:sha256:=!"
if /I not "!REMOTEHASH!"=="!LOCALHASH!" (
  echo [PUBLISH] STOPPED: the uploaded asset does NOT match the file that was just built.
  exit /b 1
)

rem Guarantee the "one installer only" promise made in README.md.
set "ASSETCOUNT=0"
for /f "usebackq delims=" %%A in (`gh release view !TAG! --repo !REPO! --json assets --jq ".assets[].name" 2^>nul`) do set /a ASSETCOUNT+=1
if not "!ASSETCOUNT!"=="1" (
  echo [PUBLISH] STOPPED: release !TAG! carries !ASSETCOUNT! assets; exactly one ^(%OUTPUT_EXE%^) was expected.
  exit /b 1
)

set "RELEASECOUNT=0"
for /f "usebackq delims=" %%R in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do set /a RELEASECOUNT+=1
if not "!RELEASECOUNT!"=="1" (
  echo [PUBLISH] WARNING: !RELEASECOUNT! releases exist; expected exactly one.
)

echo.
echo ###########################################################
echo SUCCESS: release !TAG! is live and is the only release.
echo Download: https://github.com/!REPO!/releases/latest/download/%OUTPUT_EXE%
echo SHA256:   !LOCALHASH!
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

move /y "%FINAL_DIR%\%PROJECT_EXE%" "%OUTPUT_DIR%\%OUTPUT_EXE%"
if errorlevel 1 exit /b 1

call :PURGE_COMPILED_EXTRAS
if errorlevel 1 exit /b 1
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
exit /b 0

:PURGE_COMPILED_EXTRAS
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" (
    rd /s /q "%%~fF" 2>nul
    del /f /q "%%~fF" 2>nul
  )
)
exit /b 0

:VALIDATE_COMPILED_OUTPUT
if not exist "%OUTPUT_DIR%\%OUTPUT_EXE%" exit /b 1
set "EXTRA=0"
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" set /a EXTRA+=1
)
if not "!EXTRA!"=="0" (
  echo ERROR: %OUTPUT_DIR% must contain only %OUTPUT_EXE%; found !EXTRA! extra item^(s^).
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
dotnet clean RDPVault\RDPVault.csproj -c Release -r win-x64 --nologo -v q >nul 2>&1
exit /b 0
