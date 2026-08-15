@echo off
setlocal
echo =========================================
echo  UNINSTALLING RDP VAULT
echo =========================================

taskkill /F /IM RDPVault.exe /T 2>nul
set "INSTALL_DIR=%LOCALAPPDATA%\RDPVault"
set "REG_PATH=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault"

echo Removing from appwiz.cpl...
reg delete "%REG_PATH%" /f 2>nul

echo Removing shortcuts...
del /f /q "%USERPROFILE%\Desktop\RDP Vault.lnk" 2>nul

echo Removing application files...
del /f /q "%INSTALL_DIR%\RDPVault.exe" 2>nul
:: The uninstaller script will be left behind because it is running, but we can schedule its deletion
start /b cmd /c "timeout /t 2 >nul & rd /s /q "%INSTALL_DIR%""

echo.
echo SUCCESS: RDP Vault uninstalled.
pause
