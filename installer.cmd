@echo off
echo Installing RDP Vault...
mkdir "%USERPROFILE%\Desktop\RDP Vault" 2>nul
copy /y ".\compiled\RDPVault.exe" "%USERPROFILE%\Desktop\RDP Vault\RDPVault.exe"
echo Installed to Desktop\RDP Vault.
pause
