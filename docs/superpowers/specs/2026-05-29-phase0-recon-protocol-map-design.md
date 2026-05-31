# Phase 0 — Recon & Protocol Map (Design)

**Project:** Black Ice independent server (open-source interoperability project)
**Phase:** 0 of 5 — Reconnaissance & Protocol Mapping
**Date:** 2026-05-29
**Status:** Approved design — pending spec review

---

## 1. Context

Black Ice is a Unity (Mono backend) co-op FPS/RPG whose multiplayer is built on
**Photon PUN** (`PhotonUnityNetworking`, `Photon3Unity3D`, `PhotonRealtime`), not raw
peer-to-peer. Clients connect to Photon Cloud and elect a **master client** that holds
authority over the shared world (enemy spawns, AI, damage, loot). Static analysis of
`Assembly-CSharp.dll` shows a substantial networked surface: **85 `[PunRPC]` methods**,
6 `OnPhotonSerializeView` continuous-sync components, networked enemies/turrets/buildings/
inventory, and a `PhotonNetwork.OfflineMode` single-player path (used 32×) that doubles as
a reference implementation of the world simulation.

The end goal is a from-scratch C#/.NET server that speaks Black Ice's Photon protocol so
the game can run independent of Photon Cloud, eventually taking authority away from the
master client to enable real server-side anti-cheat and custom features.

**Phase 0 produces the reference document every later phase builds on.** It is research +
documentation + tooling; it ships no server code.

## 2. Goals & Deliverables

| Deliverable | Location | Description |
|---|---|---|
| Protocol specification | `docs/protocol/` | Written spec of the connect/auth flow, operation/event/parameter codes, RPC catalog, serialize-view layouts, instantiated prefabs, custom properties |
| Recon tooling | `tools/` | Static-catalog extractor, in-process op-logger mod, capture-parsing scripts (C#/.NET; throwaway Python allowed for capture munging) |
| Capture corpus | `captures/` | Decoded session logs (gitignored); a small sanitized sample committed as fixtures |
| Toolkit setup | `docs/debugging.md` | Documented debugging/instrumentation workflow (below) |

**Definition of done:** We can describe end-to-end, on paper, how the client finds and
authenticates to a server, joins a room, and which bytes represent representative gameplay
events ("player fired weapon", "enemy took damage", "item dropped") — enough that Phase 1
can implement the connect flow without further reversing.

## 3. Repository & open-source hygiene

- Repo at `C:\Users\natew\OneDrive\Documentos\blackice-re\`, working name **`BlackIce.Server`**.
- `git init`; licensed **GPLv3** (keeps community forks open, TrinityCore-style).
- **`.gitignore` excludes ALL game-derived material**: original DLLs, decompiled `.cs`,
  Unity asset dumps, raw packet captures. These remain local analysis-only artifacts.
- `README` + `NOTICE` state this is an independent, clean-room-style interoperability
  project for a game the operator owns; it ships only original code and protocol docs.
  Precedents: OpenRA, TrinityCore.
- Decompiled sources already produced live at `blackice-re/decompiled/` (gitignored).

## 4. Recon methodology — three complementary prongs

### 4a. Static catalog (no game running)
Parse the decompiled `Assembly-CSharp` and the Photon DLLs to enumerate:
- Every `[PunRPC]` method: name, owning class, signature, and whether it is gated on
  `IsMasterClient` (authority-relevant).
- Every `OnPhotonSerializeView` implementation and the field read/write order (payload layout).
- Every `PhotonNetwork.Instantiate` / `InstantiateRoomObject` prefab name.
- Custom room/player properties (`SetCustomProperties` keys).
- **Photon enums extracted verbatim**: `OperationCode`, `EventCode`, `ParameterCode`,
  `StatusCode` from `PhotonRealtime`/`Photon3Unity3D`. These are the protocol's vocabulary.

Output: regenerable tables in `docs/protocol/generated/`.

### 4b. In-process operation logger
A **BepInEx + HarmonyX** plugin that patches the client's `PhotonPeer` send/receive path
to dump structured, **post-decryption** operations and events (op code, parameters, channel,
reliability) to disk while the live client is driven through connect → lobby → room →
gameplay. This sidesteps Photon's datagram encryption and yields semantic data the wire
cannot.

### 4c. Wire capture (transport truth)
`Wireshark`/`tshark` UDP capture to document the reliable-UDP **command framing** that the
in-process view cannot see: channels, sequencing, fragmentation/reassembly, and the
ack/reliability model. Cross-referenced against 4b by timestamp.

> Coverage note: a single instrumented client (4b) plus the static catalog (4a) gives
> near-complete coverage even solo. Two clients (operator has full access to launch the
> game as needed) mainly confirm event direction and timing.

## 5. Spec output format

Markdown, tables-first, one document per concern, with byte-layout diagrams where relevant:
- `01-connection-flow.md` — Name Server → Master Server → Game Server handshake & auth
- `02-operations.md` — operation/event/parameter code reference
- `03-rpc-catalog.md` — the 85 RPCs, signatures, authority gating
- `04-serialization.md` — GpBinary types + serialize-view payload layouts
- `05-transport.md` — reliable-UDP command framing
- `generated/` — machine-extracted catalog tables (regenerable from 4a)

## 6. Debugging & instrumentation toolkit

All protocol logic is **managed C#** on the Mono runtime; the only native modules
(`steam_api64`, XInput, InControl) do not touch netcode. Tools are ranked accordingly.

**Primary (managed):**
- **dnSpyEx** — attach to the running game, breakpoint in decompiled C#, inspect/modify
  locals, edit-and-continue. Highest-value tool. Speaks Mono's soft-debugger protocol.
- **Mono soft debugger enablement** — adjust Unity `boot.config` / debugger-agent so
  dnSpyEx (or VS Code) can attach and break inside RPC handlers live.
- **BepInEx + HarmonyX** — runtime patching; hosts the §4b op-logger.

**Secondary (native / dynamic):**
- **GDB + gdbserver** *(already installed: gdb 17.2 + gdbserver)* — native attach for
  `steam_api64`, the Mono runtime, and crash triage; remote-attach workflow that carries
  over to debugging the future C# server on a Linux host. Mono `mono-gdb.py` helpers set
  up for managed-aware frames. **Secondary by design** — GDB sees native frames, not
  managed game logic.
- **Frida** — dynamic instrumentation bridging native + managed for ad-hoc tracing
  without rebuilding a mod. Part of the standard install set.

**Capture / analysis:** Wireshark/tshark (§4c), `ilspycmd` *(installed)*, System Informer
(process/handle inspection).

**Future server (Phase 1+):** .NET-native debugging via `dotnet-trace`/`dotnet-dump` +
lldb-SOS for managed; gdb/gdbserver for native crashes and remote attach.

## 7. Execution order (first steps for the plan)

1. Initialize repo, `.gitignore`, README/NOTICE, license placeholder.
2. Install toolkit: dnSpyEx, BepInEx into the game, Frida; wire up GDB attach + Mono
   helpers; confirm Wireshark/tshark capture works.
3. Build & run the static-catalog extractor (§4a) → `docs/protocol/generated/`.
4. Build the BepInEx op-logger (§4b); drive the client and collect a connect→room session.
5. Capture wire framing (§4c) for the same session; correlate.
6. (If feasible) two-client gameplay session for in-room RPC/state coverage.
7. Author the `docs/protocol/0X-*.md` spec documents from the collected evidence.

## 8. Out of scope (Phase 0)

- Any server implementation, transport code, or client redirect (Phase 1).
- Relaying or simulating game state (Phases 2–3).
- Anti-cheat logic (Phase 4).
- Decryption-key recovery beyond what in-process logging makes unnecessary.
