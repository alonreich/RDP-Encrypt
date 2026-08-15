# RDP Vault

RDP Vault is a zero-trace, mathematically uncrackable, pure NativeAOT executable that stores and launches your Remote Desktop profiles with military-grade precision.

It completely bypasses standard Windows Credential Manager limitations, scrubs all connection history from the operating system, and provides absolute security over your hosts.

## Core Architecture

- **NativeAOT Silicon**: Compiled natively ahead-of-time. No .NET runtime required. Just drop the `installer.cmd` and execute.
- **TPM Hardware Signatures**: Windows DPAPI has been ripped out. Quick Unlocks use raw physical cryptography from your motherboard's Trusted Platform Module (TPM). Without your physical biometric (Windows Hello), the TPM locks down and Mimikatz RAM-scraping is mathematically impossible.
- **Paper Recovery Keys**: When a vault is created, a 24-word offline cryptographic seed phrase is generated for catastrophic cross-machine recovery.
- **Vault Self-Destruct**: Exceed the configurable failed-attempt limit, and the vault structurally obliterates itself.
- **Direct Shortcuts**: Generate `.rdpvlink` shortcut files. Map them to StreamDeck or double-click them to instantly pipeline into your remote machine via background IPC—bypassing all UI elements.
- **BitLocker Enforcement**: The application refuses to execute if the host drive is not encrypted.

## Installation & Deployment

1. Download the latest automated NativeAOT binary:
   [Download RDP Vault](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.exe)
2. Run `installer.cmd` to register `.rdpvlink` bindings directly into the Windows Registry natively without UAC prompts.
3. The executable inherently binds to `appwiz.cpl` allowing native "Add/Remove Programs" modifications.

## Uninstallation

Execute `uninstaller.cmd` directly or uninstall via Windows `appwiz.cpl`. The teardown process:
1. Force-terminates all active IPC pipes.
2. Shreds temporary `.rdp` configurations.
3. Completely destroys the `RDPVault` payload directory.
4. Executes a process-detaching batch deletion to permanently wipe the uninstaller itself from your disk.