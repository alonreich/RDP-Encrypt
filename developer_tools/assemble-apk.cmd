@echo off
REM ============================================================================
REM  assemble-apk.cmd  -  reassemble compiled\RDPVault.apk from its 3 parts
REM
REM  The APK is 41 MiB. The Claude file bridge caps a single written file at
REM  20 MiB, so it was split into three ~13.7 MiB parts. This script joins them
REM  back byte-for-byte, verifies the SHA-256, and deletes the parts.
REM
REM  Run it from anywhere:  compiled\assemble-apk.cmd
REM ============================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "EXPECTED=233d609d10ac3999ab48d3e73754a18de072c392724f21849108cb114ac7b3dc"
set "OUT=RDPVault.apk"

for %%P in (0 1 2) do (
  if not exist "RDPVault.apk.part%%P" (
    echo [ERROR] Missing RDPVault.apk.part%%P - cannot reassemble.
    exit /b 1
  )
)

echo Joining 3 parts into %OUT% ...
copy /b "RDPVault.apk.part0"+"RDPVault.apk.part1"+"RDPVault.apk.part2" "%OUT%" >nul
if errorlevel 1 (
  echo [ERROR] copy /b failed.
  exit /b 1
)

echo Verifying SHA-256 ...
set "ACTUAL="
for /f "skip=1 tokens=* delims=" %%H in ('certutil -hashfile "%OUT%" SHA256') do (
  if not defined ACTUAL set "ACTUAL=%%H"
)
set "ACTUAL=%ACTUAL: =%"

if /I "!ACTUAL!"=="%EXPECTED%" (
  echo.
  echo   SHA-256 OK: !ACTUAL!
  echo   %OUT% is complete and matches the build.
  del /q "RDPVault.apk.part0" "RDPVault.apk.part1" "RDPVault.apk.part2" 2>nul
  echo   Part files deleted.
  echo.
  echo Install with:  adb install -r "%~dp0%OUT%"
  exit /b 0
) else (
  echo.
  echo   [ERROR] SHA-256 MISMATCH - the APK is corrupt. Do not install it.
  echo   expected %EXPECTED%
  echo   actual   !ACTUAL!
  del /q "%OUT%" 2>nul
  exit /b 1
)
