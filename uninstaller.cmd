@echo off
setlocal
echo =========================================
echo  UNINSTALLING RDP VAULT
echo =========================================

echo Terminating active Vault instances...
taskkill /F /IM RDPVault.exe /T >nul 2>&1

set "INSTALL_DIR=%LOCALAPPDATA%\RDPVault"
set "REG_PATH=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault"

echo Removing registry hooks...
reg delete "%REG_PATH%" /f >nul 2>&1
reg delete "HKCU\Software\Classes\.rdpvlink" /f >nul 2>&1
reg delete "HKCU\Software\Classes\RDPVault.Link" /f >nul 2>&1

echo Removing Desktop shortcuts...
del /f /q "%USERPROFILE%\Desktop\RDP Vault.lnk" >nul 2>&1

echo Scrubbing temporary RDP configuration traces...
del /f /q "%TEMP%\rdpv_*.rdp" >nul 2>&1

if exist "%INSTALL_DIR%\vault.rdpv" (
    echo Rescuing your vault to Documents\RDP Vault Backups...
    mkdir "%USERPROFILE%\Documents\RDP Vault Backups" 2>nul
    copy /y "%INSTALL_DIR%\vault.rdpv" "%USERPROFILE%\Documents\RDP Vault Backups\vault.rdpv" >nul
)

echo SUCCESS: RDP Vault cleanly uninstalled.
echo Closing window and cleaning directory...

ping 127.0.0.1 -n 2 >nul
cd /d "%USERPROFILE%"
(goto) 2>nul & rd /s /q "%INSTALL_DIR%"
