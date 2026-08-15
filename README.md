# RDP Vault

RDP Vault is a zero-trace, mathematically uncrackable, pure NativeAOT executable that stores and launches your Remote Desktop profiles with military-grade precision.

It completely bypasses standard Windows Credential Manager limitations, scrubs all connection history from the operating system, and provides absolute security over your hosts.

## Core Architecture

- **NativeAOT Silicon**: Compiled natively ahead-of-time. No .NET runtime required. A single standalone `.exe` handles everything.
- **TPM Hardware Signatures**: Windows DPAPI has been ripped out. Quick Unlocks use raw physical cryptography from your motherboard's Trusted Platform Module (TPM). Without your physical biometric (Windows Hello), the TPM locks down and Mimikatz RAM-scraping is mathematically impossible.
- **Paper Recovery Keys**: When a vault is created, a 24-word offline cryptographic seed phrase is generated for catastrophic cross-machine recovery.
- **Vault Self-Destruct**: Exceed the configurable failed-attempt limit, and the vault structurally obliterates itself.
- **Direct Shortcuts**: Generate `.rdpvlink` shortcut files. Map them to StreamDeck or double-click them to instantly pipeline into your remote machine via background IPC—bypassing all UI elements.
- **BitLocker Enforcement**: The application refuses to execute if the host drive is not encrypted.

## Installation & Deployment

1. Download the latest automated NativeAOT binary:
   [Download RDP Vault](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.exe)
2. Launch the downloaded `RDPVault.exe`. A Setup Wizard will intercept startup.
3. Choose **"Install to this PC"** for a full native deployment, or **"Run Portably"** to securely operate out of a USB drive with zero traces on the host machine.
4. If Installed, the executable natively creates Desktop/Start Menu shortcuts, copies itself to `%LocalAppData%`, and binds to `appwiz.cpl` allowing native "Add/Remove Programs" modifications.

## Uninstallation

Uninstall directly via Windows **Programs and Features (`appwiz.cpl`)**. The teardown process:
1. Triggers the internal `--uninstall` command gracefully.
2. Unregisters all `.rdpvlink` handlers and deletes Registry footprints.
3. Completely destroys the `%LocalAppData%\RDPVault` payload directory.
4. Executes a process-detaching batch deletion command to permanently shred the running executable from your disk silently.