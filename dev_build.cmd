@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

rem ---------------------------------------------------------------------------
rem  dev_build.cmd — Local Development Build (Zero Git/GitHub Interaction)
rem  Compiles RDP Vault to .\compiled\ locally. Does not touch git tags,
rem  does not publish to GitHub, and does not wipe pre-existing compiled binaries.
rem ---------------------------------------------------------------------------

if "%~1"=="--internal-log" goto :run_logged
if exist build.log del /f /q build.log
powershell -NoProfile -Command "& { & '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build.log; exit $LASTEXITCODE }"
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  LOCAL DEV BUILD FAILED.
  echo ###########################################################
  pause
)

  popd >nul

  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\SetConsoleFont.ps1" <nul

exit /b %RC%

:run_logged
shift
setlocal enabledelayedexpansion
cd /d "."

set "DO_PUBLISH=0"

set "PROJECT_FILE=RDPVault\RDPVault.csproj"
set "PROJECT_EXE=RDPVault.exe"
set "OUTPUT_EXE=RDPVault.exe"
set "OUTPUT_DIR=.\compiled"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=false"
set "PUBLISH_SF_ARGS=-p:PublishSingleFile=true -p:SelfContained=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:ErrorsOnly"

echo ###########################################################
echo PREPARING LOCAL BUILD ENVIRONMENT...
echo ###########################################################
call :TERMINATE_PROCESSES
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
call :CLEAN_ALL

echo.
echo ###########################################################
echo BUILDING RDP Vault: Self-Contained SingleFile win-x64 (Local Dev)
echo ###########################################################
call :BUILD_SINGLEFILE
if errorlevel 1 exit /b 1

call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Local dev build completed successfully.
echo.
echo Single EXE: %OUTPUT_DIR%\%OUTPUT_EXE%
echo Log file:   .\build.log
echo [PUBLISH] Skipped. Git and GitHub were not touched.
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
echo Verified %OUTPUT_DIR% contains exactly %OUTPUT_EXE%.
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
