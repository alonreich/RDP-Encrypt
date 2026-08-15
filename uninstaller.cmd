@echo off
echo Uninstalling RDP Vault...
taskkill /F /IM RDPVault.exe /T 2>nul
rd /s /q "%USERPROFILE%\Desktop\RDP Vault" 2>nul
echo Uninstalled.
pause
