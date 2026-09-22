// FreeRdpClient.cs
//
// INTENTIONALLY EMPTY - retained only so existing build scripts and diffs keep resolving.
//
// RDP Vault for Android does NOT embed an RDP protocol stack. There is no FreeRDP,
// no native shared library and no socket-level RDP implementation in this app.
// All remote sessions are handed off to an external, user-installed RDP client
// (Microsoft Remote Desktop or aRDP) through a standard Android rdp:// VIEW Intent.
// See Rdp/RdpLauncher.cs for the only connection path that exists.
//
// This file declares no types on purpose. Do not reintroduce P/Invoke bindings here.
