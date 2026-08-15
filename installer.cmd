@echo off
setlocal
echo =========================================
echo  INSTALLING RDP VAULT
echo =========================================

set "INSTALL_DIR=%LOCALAPPDATA%\RDPVault"
mkdir "%INSTALL_DIR%" 2>nul
copy /y ".\compiled\RDPVault.exe" "%INSTALL_DIR%\RDPVault.exe" >nul
copy /y ".\uninstaller.cmd" "%INSTALL_DIR%\uninstaller.cmd" >nul

set "REG_PATH=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault"
reg add "%REG_PATH%" /v DisplayName /d "RDP Vault" /f >nul
reg add "%REG_PATH%" /v DisplayIcon /d "\"%INSTALL_DIR%\RDPVault.exe\",0" /f >nul
reg add "%REG_PATH%" /v UninstallString /d "\"%INSTALL_DIR%\uninstaller.cmd\"" /f >nul
reg add "%REG_PATH%" /v DisplayVersion /d "1.0.0" /f >nul
reg add "%REG_PATH%" /v Publisher /d "Alon Reich" /f >nul

echo Creating Desktop Shortcut...
powershell -NoProfile -Command "$wshell = New-Object -ComObject WScript.Shell; $s = $wshell.CreateShortcut('%USERPROFILE%\Desktop\RDP Vault.lnk'); $s.TargetPath = '%INSTALL_DIR%\RDPVault.exe'; $s.WorkingDirectory = '%INSTALL_DIR%'; $s.Save()"

echo.
echo SUCCESS: RDP Vault installed and registered in appwiz.cpl.
pause
