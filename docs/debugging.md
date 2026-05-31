# Debugging & Instrumentation Toolkit

All of Black Ice's protocol logic is managed C# on the Mono runtime
(`mono-2.0-bdwgc.dll`, Unity 2020.3.49, CLR 4.x, 64-bit). Native modules
(`steam_api64`, XInput, InControl) do not touch netcode. Tools are ranked by how much they
earn their keep on this target.

## Primary — managed
- **dnSpyEx 6.5.1** (`third-party/dnSpyEx/dnSpy.exe`): decompile + attach + breakpoint in C#.
  Attach: Debug > Attach to Process > select `Black Ice` (Unity/Mono). See
  [Mono soft debugger](#mono-soft-debugger) below for enabling the agent.
- **BepInEx 5.4.23.5** (installed in `<GAME>/BepInEx`): plugin host for the op-logger.
  Confirmed loading via doorstop (`winhttp.dll` + `doorstop_config.ini`); logs to
  `<GAME>/BepInEx/LogOutput.log`. HarmonyX (`0Harmony.dll`) ships in `BepInEx/core`.

## Secondary — native / dynamic
- **GDB 17.2 + gdbserver** (already on PATH): native attach for `steam_api64`, the Mono
  runtime, and crash triage; the remote-attach workflow is reused for the future C# server.
  See [GDB attach](#gdb-native-attach) below. Native frames only — not game logic.
- **Frida 17.9.11** (frida-tools 14.8.2, via pip): dynamic native+managed tracing.
  `frida-trace -n "Black Ice.exe"`.

## Capture / analysis
- **tshark/Wireshark**: UDP transport framing — see `tools/capture/capture.ps1`.
- **ilspycmd 8.2.0.7535** (global dotnet tool): static decompile to `<GAME>` DLLs ->
  `decompiled/` (analysis-only, gitignored).

## Environment notes
- `curl` downloads on this machine require `--ssl-no-revoke` (schannel revocation-check
  failure). NuGet/`dotnet` restore is unaffected.

## Mono soft debugger
Enabled via `<GAME>/Black Ice_Data/boot.config` (backed up as `boot.config.bak`):

```
player-connection-debug=1
wait-for-managed-debugger=0
```

dnSpyEx attaches through Unity's PlayerConnection discovery — open
`Assembly-CSharp.dll` in dnSpyEx, then **Debug > Start Debugging > Unity** (or
**Attach to Process > Unity**). Confirm by breakpointing a hot method such as
`MouseLook.Update` and moving the mouse in-game (one-time manual check; the game's
own input is required, so it is not part of the automated run).

> Do **not** also pass `MONO_ENV_OPTIONS=--debugger-agent=...` with a fixed port — it
> conflicts with `player-connection-debug` and the player exits on launch. Use one or the
> other; the boot.config path above is the supported one.

## GDB native attach
Verified working (gdb 17.2). The game is launched first, then:

```bash
GDB=".../mingw64/bin/gdb.exe"
BCPID=$(powershell.exe -NoProfile -Command "(Get-Process 'Black Ice' | Select -First 1).Id" | tr -d '\r')
"$GDB" -p "$BCPID" -batch -ex "info sharedlibrary mono-2.0" -ex "bt" -ex "detach"
```

gdb attaches, enumerates all game threads, lists `mono-2.0-bdwgc.dll`, prints a native
backtrace, and detaches cleanly (exit 0). Managed assemblies (`Assembly-CSharp`, Photon
DLLs) are Mono images, **not** native modules, so they do not appear in the shared-library
or process-module lists — only `mono-2.0-bdwgc.dll` does. Use gdb for native/runtime/Steam
crash triage; use dnSpyEx for managed game logic.

## Future server (Phase 1+)
- `dotnet-trace` / `dotnet-dump` + lldb-SOS for managed debugging; gdb/gdbserver for native
  crashes and remote attach on a Linux host.
