# Phase 0 — Recon & Protocol Map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a written specification of Black Ice's Photon protocol plus the reusable recon tooling and capture corpus that every later phase of the independent server builds on.

**Architecture:** Three complementary recon prongs — (a) a Mono.Cecil-based static-catalog extractor that reads the game's own assemblies, (b) a BepInEx/HarmonyX in-process op-logger that records decrypted Photon operations from the live client, and (c) `tshark` wire capture for transport framing — feeding a set of Markdown protocol documents. Ships documentation + tooling only; no server code.

**Tech Stack:** .NET 8 (extractor) / netstandard2.0 (BepInEx plugin), Mono.Cecil, BepInEx 5.4.23.5 (Mono x64), HarmonyX, dnSpyEx 6.5.1, Frida (frida-tools 14.8.2), gdb 17.2 + gdbserver, Wireshark/tshark, Python 3.12.

**Environment notes (this machine):**
- `curl` needs `--ssl-no-revoke` (schannel revocation-check failure on downloads).
- Game root: `C:\Program Files (x86)\Steam\steamapps\common\Black Ice` (referred to below as `$GAME`).
- Repo root: `C:\Users\natew\OneDrive\Documentos\blackice-re` (referred to below as `$REPO`).
- Decompiled analysis sources already exist at `$REPO/decompiled/` (gitignored).
- Run Bash-tool commands from `$REPO` unless stated. `git` user is configured in Task 1.

---

## File Structure

```
blackice-re/
  LICENSE                         # GPLv3 (Task 1)
  README.md  NOTICE               # OSS framing (Task 1)
  .gitignore                      # exists — excludes game-derived material
  docs/
    debugging.md                  # toolkit + debugger workflow (Tasks 2,3)
    protocol/
      00-overview.md  01-connection-flow.md  02-operations.md
      03-rpc-catalog.md  04-serialization.md  05-transport.md   # (Task 8)
      generated/                  # machine-extracted tables (Task 4 output)
  tools/
    BlackIce.Recon.sln            # (Task 4)
    BlackIce.Recon.Catalog/       # Mono.Cecil extractor (Task 4)
      BlackIce.Recon.Catalog.csproj  Program.cs  CecilExtensions.cs  Records.cs
    BlackIce.Recon.Catalog.Tests/ # characterization tests (Task 4)
    capture/
      parse_oplog.py  test_parse_oplog.py   # correlation parser (Task 7)
      capture.ps1                 # tshark wrapper (Task 6)
  plugins/
    BlackIce.OpLogger/            # BepInEx plugin (Task 5)
      BlackIce.OpLogger.csproj  Plugin.cs  PhotonPatches.cs
  third-party/                    # downloaded tools (gitignored)
  decompiled/                     # gitignored (exists)
  captures/                       # gitignored corpus; sample fixture committed
```

---

## Task 1: Repository bootstrap

**Files:**
- Create: `$REPO/LICENSE`, `$REPO/README.md`, `$REPO/NOTICE`
- Modify: `$REPO/.gitignore` (add `third-party/`)

- [ ] **Step 1: Configure git identity for this repo**

```bash
cd "$REPO"
git config user.name "Black Ice Server"
git config user.email "<your email>"
```

- [ ] **Step 2: Add the GPLv3 license text**

Download the canonical text (verifiable, deterministic):

```bash
curl -sS --ssl-no-revoke -o "$REPO/LICENSE" https://www.gnu.org/licenses/gpl-3.0.txt
head -1 "$REPO/LICENSE"
```

Expected: `                    GNU GENERAL PUBLIC LICENSE`

- [ ] **Step 3: Write README.md**

```markdown
# BlackIce.Server

An independent, open-source server implementation for the Unity game **Black Ice**,
created for interoperability, preservation, and server-authoritative anti-cheat.

Black Ice's multiplayer runs on Photon PUN with a *master-client authority* model:
one player simulates the shared world for everyone, which makes client-side cheating
trivial. This project reimplements the server side of that protocol so the game can run
on infrastructure you control, and so world authority can move from an untrusted player
to the server.

## Status

**Phase 0 — Reconnaissance & Protocol Map.** Documenting the protocol and building the
recon tooling. No server yet. See `docs/superpowers/plans/`.

## Legal / scope

This is a clean, independent interoperability project in the tradition of OpenRA and
TrinityCore. It contains **only original code and protocol documentation**. It does not
contain, redistribute, or depend on the game's copyrighted binaries, assets, or
decompiled source — those are analysis-only artifacts kept locally and excluded from
version control. You must own a legitimate copy of Black Ice to use this software.

## License

GPLv3 — see `LICENSE`.
```

- [ ] **Step 4: Write NOTICE**

```
BlackIce.Server
Copyright (C) 2026 BlackIce.Server contributors

This program is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License v3.0 as published by the Free Software
Foundation. See the LICENSE file for details.

"Black Ice" is the property of its respective owner. This project is not affiliated
with or endorsed by the game's developer. It is an independent interoperability and
preservation effort and ships no copyrighted game material.
```

- [ ] **Step 5: Exclude downloaded tools from git**

```bash
printf '\n# --- Downloaded third-party tools ---\nthird-party/\n' >> "$REPO/.gitignore"
grep -c "third-party" "$REPO/.gitignore"
```

Expected: `1`

- [ ] **Step 6: Verify nothing game-derived is staged, then commit**

```bash
cd "$REPO"
git add LICENSE README.md NOTICE .gitignore
git status -s          # MUST show only LICENSE, README.md, NOTICE, .gitignore
git commit -m "chore: bootstrap repository (GPLv3, README, NOTICE)"
```

Expected: `git status -s` lists no `.dll`, `.cs` from `decompiled/`, or `captures/` entries.

---

## Task 2: Acquire and verify the instrumentation toolkit

**Files:**
- Create: `$REPO/docs/debugging.md`
- Downloads into `$REPO/third-party/` (gitignored) and `$GAME` (BepInEx)

- [ ] **Step 1: Create the tools directory**

```bash
mkdir -p "$REPO/third-party"
```

- [ ] **Step 2: Download BepInEx 5.4.23.5 (Mono, x64)**

```bash
cd "$REPO/third-party"
URL=$(curl -sS --ssl-no-revoke https://api.github.com/repos/BepInEx/BepInEx/releases/tags/v5.4.23.5 \
  | grep -oE '"browser_download_url": *"[^"]*BepInEx_win_x64[^"]*\.zip"' | grep -oE 'https[^"]*' | head -1)
echo "Downloading: $URL"
curl -sSL --ssl-no-revoke -o BepInEx_x64.zip "$URL"
ls -la BepInEx_x64.zip
```

Expected: a non-empty `BepInEx_x64.zip` (~5 MB).

- [ ] **Step 3: Install BepInEx into the game folder**

```bash
cd "$REPO/third-party"
unzip -o BepInEx_x64.zip -d "/c/Program Files (x86)/Steam/steamapps/common/Black Ice"
ls "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx"
```

Expected: `BepInEx` folder now exists in the game directory (contains `core/`).

- [ ] **Step 4: Generate BepInEx config by launching the game once**

Launch the game, wait ~20s for it to reach the menu, then close it. (Bash tool can launch via the Steam exe path; if the harness cannot drive the GUI, ask the operator to launch once.)

```bash
ls "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/config/BepInEx.cfg"
ls "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/LogOutput.log"
```

Expected: both files exist — confirms BepInEx loaded into the game process.

- [ ] **Step 5: Download dnSpyEx 6.5.1 (net win64)**

```bash
cd "$REPO/third-party"
URL=$(curl -sS --ssl-no-revoke https://api.github.com/repos/dnSpyEx/dnSpy/releases/tags/v6.5.1 \
  | grep -oE '"browser_download_url": *"[^"]*win64[^"]*\.zip"' | grep -oE 'https[^"]*' | head -1)
echo "Downloading: $URL"
curl -sSL --ssl-no-revoke -o dnSpyEx.zip "$URL"
unzip -o dnSpyEx.zip -d dnSpyEx
ls dnSpyEx/dnSpy.exe
```

Expected: `dnSpyEx/dnSpy.exe` exists.

- [ ] **Step 6: Install Frida**

```bash
pip install "frida-tools==14.8.2"
frida --version
```

Expected: a version string prints (frida core version).

- [ ] **Step 7: Write docs/debugging.md (toolkit reference)**

Document each tool, its role, and the exact invocation. Content:

```markdown
# Debugging & Instrumentation Toolkit

All of Black Ice's protocol logic is managed C# on the Mono runtime
(`mono-2.0-bdwgc.dll`). Native modules (`steam_api64`, XInput, InControl) do not touch
netcode. Tools are ranked by how much they earn their keep on this target.

## Primary — managed
- **dnSpyEx** (`third-party/dnSpyEx/dnSpy.exe`): decompile + attach + breakpoint in C#.
  Attach: Debug > Attach to Process > select `Black Ice` (Unity/Mono). See Task 3 for
  enabling the Mono debugger agent.
- **BepInEx 5.4.23.5** (installed in `$GAME/BepInEx`): plugin host for the op-logger.
  Logs to `$GAME/BepInEx/LogOutput.log`. HarmonyX ships with it.

## Secondary — native / dynamic
- **GDB 17.2 + gdbserver** (already on PATH): native attach for `steam_api64`, the Mono
  runtime, and crash triage; remote-attach workflow reused for the future C# server.
  See Task 3 for the attach recipe and Mono helper. Native frames only — not game logic.
- **Frida 14.8.2**: dynamic native+managed tracing. `frida-trace -n "Black Ice.exe"`.

## Capture / analysis
- **tshark/Wireshark**: UDP transport framing (Task 6).
- **ilspycmd** (global dotnet tool): static decompile to `$REPO/decompiled/` (analysis-only).

## Future server (Phase 1+)
- `dotnet-trace` / `dotnet-dump` + lldb-SOS for managed; gdb/gdbserver for native crashes
  and remote attach on a Linux host.
```

- [ ] **Step 8: Commit the docs (tools themselves are gitignored)**

```bash
cd "$REPO"
git add docs/debugging.md
git status -s     # MUST NOT list anything under third-party/
git commit -m "docs: instrumentation toolkit reference and install"
```

---

## Task 3: Enable the Mono soft debugger and verify the GDB workflow

**Files:**
- Modify: `$GAME/Black Ice_Data/boot.config` (analysis-only; documented, not committed)
- Modify: `$REPO/docs/debugging.md` (append the verified recipe)

- [ ] **Step 1: Back up boot.config**

```bash
cp "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data/boot.config" \
   "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data/boot.config.bak"
cat "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data/boot.config"
```

Note the current contents (recorded for restore).

- [ ] **Step 2: Enable the Unity Mono debugger agent**

Append the debugger flags so Mono opens the soft-debugger port:

```bash
BC="/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data/boot.config"
printf 'player-connection-debug=1\nwait-for-managed-debugger=0\n' >> "$BC"
tail -3 "$BC"
```

- [ ] **Step 3: Verify dnSpyEx can attach and hit a managed breakpoint**

Launch the game. In dnSpyEx: open `$GAME/Black Ice_Data/Managed/Assembly-CSharp.dll`, then
Debug > Attach to Process > Unity. Set a breakpoint in a frequently-hit method (e.g.
`MouseLook.Update` or any `Update`). Move the mouse in-game; confirm the breakpoint hits.

Expected: dnSpyEx pauses the game at the breakpoint and shows local variables.
If attach fails: try the env-var launch fallback — set
`MONO_ENV_OPTIONS=--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:56000`
before launching, and attach to that port. Record which method worked.

- [ ] **Step 4: Verify the GDB native attach recipe**

While the game is running:

```bash
gdb -p $(powershell.exe -NoProfile -Command "(Get-Process 'Black Ice').Id" | tr -d '\r') \
    -batch -ex "info sharedlibrary mono" -ex "bt" 2>&1 | head -30
```

Expected: gdb attaches, lists `mono-2.0-bdwgc.dll` among shared libraries, prints a native
backtrace. (Confirms native attach works; managed frames require the Mono helper, noted
below.) Detach cleanly.

- [ ] **Step 5: Append the verified recipe to docs/debugging.md and commit**

Record the exact attach steps that worked (boot.config vs env-var, the breakpoint method,
the gdb pid command). Then:

```bash
cd "$REPO"
git add docs/debugging.md
git commit -m "docs: verified Mono soft-debugger + gdb attach recipe"
```

> boot.config and boot.config.bak are inside the game folder and are gitignored — never committed.

---

## Task 4: Static-catalog extractor (Mono.Cecil)

**Files:**
- Create: `$REPO/tools/BlackIce.Recon.sln`
- Create: `$REPO/tools/BlackIce.Recon.Catalog/BlackIce.Recon.Catalog.csproj`
- Create: `$REPO/tools/BlackIce.Recon.Catalog/Records.cs`
- Create: `$REPO/tools/BlackIce.Recon.Catalog/CecilExtensions.cs`
- Create: `$REPO/tools/BlackIce.Recon.Catalog/Program.cs`
- Test: `$REPO/tools/BlackIce.Recon.Catalog.Tests/BlackIce.Recon.Catalog.Tests.csproj`
- Test: `$REPO/tools/BlackIce.Recon.Catalog.Tests/CatalogTests.cs`

> The extractor reads the game's own assemblies with Mono.Cecil (no decompiled text needed).
> Tests are **characterization tests**: they assert facts we already verified by grep
> (≥80 `[PunRPC]` methods; a `OperationCode` constant set with an `Authenticate` member),
> pinning the extractor's behavior against the real binaries.

- [ ] **Step 1: Scaffold the solution and projects**

```bash
cd "$REPO/tools"
dotnet new console -n BlackIce.Recon.Catalog -o BlackIce.Recon.Catalog --framework net8.0
dotnet new xunit   -n BlackIce.Recon.Catalog.Tests -o BlackIce.Recon.Catalog.Tests --framework net8.0
dotnet new sln -n BlackIce.Recon
dotnet sln add BlackIce.Recon.Catalog BlackIce.Recon.Catalog.Tests
dotnet add BlackIce.Recon.Catalog package Mono.Cecil --version 0.11.5
dotnet add BlackIce.Recon.Catalog.Tests reference BlackIce.Recon.Catalog
dotnet add BlackIce.Recon.Catalog.Tests package Mono.Cecil --version 0.11.5
```

- [ ] **Step 2: Define the record types**

Create `BlackIce.Recon.Catalog/Records.cs`:

```csharp
namespace BlackIce.Recon.Catalog;

/// <summary>A networked remote-procedure-call handler discovered via the [PunRPC] attribute.</summary>
public sealed record RpcEntry(string DeclaringType, string Method, string[] Parameters, bool ReferencesMasterClient);

/// <summary>A named protocol constant (Photon OperationCode/EventCode/ParameterCode/StatusCode member).</summary>
public sealed record ConstantEntry(string Group, string Name, object Value);

/// <summary>A type that performs continuous state replication via OnPhotonSerializeView.</summary>
public sealed record SerializeViewEntry(string DeclaringType, string[] StreamCallOrder);

/// <summary>A prefab name passed to PhotonNetwork.Instantiate / InstantiateRoomObject.</summary>
public sealed record InstantiateEntry(string DeclaringType, string Method, string PrefabName);
```

- [ ] **Step 3: Write the failing test for RPC extraction**

Create `BlackIce.Recon.Catalog.Tests/CatalogTests.cs`:

```csharp
using BlackIce.Recon.Catalog;
using Mono.Cecil;
using Xunit;

public class CatalogTests
{
    // Game assemblies live outside the repo; tests read them in place (analysis-only).
    const string GameManaged =
        @"C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed";

    static ModuleDefinition Module(string dll) =>
        ModuleDefinition.ReadModule(System.IO.Path.Combine(GameManaged, dll));

    [Fact]
    public void Finds_at_least_80_PunRPC_methods()
    {
        var rpcs = Catalog.ExtractRpcs(Module("Assembly-CSharp.dll"));
        Assert.True(rpcs.Count >= 80, $"expected >=80 RPCs, found {rpcs.Count}");
        Assert.All(rpcs, r => Assert.False(string.IsNullOrWhiteSpace(r.Method)));
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

```bash
cd "$REPO/tools"
dotnet test BlackIce.Recon.Catalog.Tests
```

Expected: FAIL — `Catalog` / `ExtractRpcs` does not exist (compile error).

- [ ] **Step 5: Implement Cecil helpers**

Create `BlackIce.Recon.Catalog/CecilExtensions.cs`:

```csharp
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BlackIce.Recon.Catalog;

internal static class CecilExtensions
{
    /// <summary>All types in a module, including nested types, flattened.</summary>
    public static IEnumerable<TypeDefinition> AllTypes(this ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var nested in t.NestedTypes) yield return nested;
        }
    }

    /// <summary>True if the method body references PhotonNetwork.IsMasterClient (authority hint).</summary>
    public static bool ReferencesMasterClient(this MethodDefinition m)
    {
        if (!m.HasBody) return false;
        foreach (var instr in m.Body.Instructions)
            if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                && instr.Operand is MethodReference mr
                && mr.Name.Contains("IsMasterClient"))
                return true;
        return false;
    }
}
```

- [ ] **Step 6: Implement the RPC extractor**

Create `BlackIce.Recon.Catalog/Program.cs` (the `Catalog` class; `Main` added in Step 13):

```csharp
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BlackIce.Recon.Catalog;

public static class Catalog
{
    public static List<RpcEntry> ExtractRpcs(ModuleDefinition module)
    {
        var result = new List<RpcEntry>();
        foreach (var type in module.AllTypes())
            foreach (var method in type.Methods)
                if (method.CustomAttributes.Any(a => a.AttributeType.Name == "PunRPC"))
                    result.Add(new RpcEntry(
                        type.FullName,
                        method.Name,
                        method.Parameters.Select(p => p.ParameterType.Name).ToArray(),
                        method.ReferencesMasterClient()));
        return result;
    }
}
```

- [ ] **Step 7: Run the RPC test to verify it passes**

```bash
cd "$REPO/tools"
dotnet test BlackIce.Recon.Catalog.Tests --filter Finds_at_least_80_PunRPC_methods
```

Expected: PASS.

- [ ] **Step 8: Write the failing test for Photon constant extraction**

Append to `CatalogTests.cs`:

```csharp
    [Fact]
    public void Extracts_Photon_OperationCode_with_Authenticate()
    {
        var consts = Catalog.ExtractNamedConstants(
            Module("PhotonRealtime.dll"),
            new[] { "OperationCode", "EventCode", "ParameterCode", "ErrorCode" });
        var ops = consts.Where(c => c.Group == "OperationCode").ToList();
        Assert.NotEmpty(ops);
        Assert.Contains(ops, c => c.Name == "Authenticate");
    }
```

- [ ] **Step 9: Run it to verify it fails**

```bash
dotnet test BlackIce.Recon.Catalog.Tests --filter Extracts_Photon_OperationCode_with_Authenticate
```

Expected: FAIL — `ExtractNamedConstants` not defined.

- [ ] **Step 10: Implement constant extraction**

Add to the `Catalog` class in `Program.cs`:

```csharp
    /// <summary>
    /// Extracts literal members (enum members and `const` fields both qualify) from the named
    /// types. Works for Photon's OperationCode/EventCode/ParameterCode/ErrorCode (const-byte
    /// classes) and StatusCode (enum) alike.
    /// </summary>
    public static List<ConstantEntry> ExtractNamedConstants(ModuleDefinition module, string[] groupTypeNames)
    {
        var wanted = new HashSet<string>(groupTypeNames);
        var result = new List<ConstantEntry>();
        foreach (var type in module.AllTypes())
        {
            if (!wanted.Contains(type.Name)) continue;
            foreach (var field in type.Fields)
                if (field.IsLiteral && field.HasConstant && field.Constant is not null)
                    result.Add(new ConstantEntry(type.Name, field.Name, field.Constant));
        }
        return result;
    }
```

- [ ] **Step 11: Run the constant test to verify it passes**

```bash
dotnet test BlackIce.Recon.Catalog.Tests --filter Extracts_Photon_OperationCode_with_Authenticate
```

Expected: PASS. (If `Authenticate` lives in a differently-named type, the test output shows
what was found — adjust the type-name list, do not weaken the assertion.)

- [ ] **Step 12: Implement serialize-view and instantiate scans (no new test gate; verified by output in Step 14)**

Add to the `Catalog` class in `Program.cs`:

```csharp
    public static List<SerializeViewEntry> ExtractSerializeViews(ModuleDefinition module)
    {
        var result = new List<SerializeViewEntry>();
        foreach (var type in module.AllTypes())
        foreach (var m in type.Methods)
        {
            if (m.Name != "OnPhotonSerializeView" || !m.HasBody) continue;
            var calls = new List<string>();
            foreach (var instr in m.Body.Instructions)
                if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                    && instr.Operand is MethodReference mr
                    && (mr.Name is "SendNext" or "ReceiveNext" or "Serialize"))
                    calls.Add(mr.Name);
            result.Add(new SerializeViewEntry(type.FullName, calls.ToArray()));
        }
        return result;
    }

    public static List<InstantiateEntry> ExtractInstantiations(ModuleDefinition module)
    {
        var result = new List<InstantiateEntry>();
        foreach (var type in module.AllTypes())
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            Instruction? prevStr = null;
            foreach (var instr in m.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Ldstr) prevStr = instr;
                if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                    && instr.Operand is MethodReference mr
                    && (mr.Name is "Instantiate" or "InstantiateRoomObject")
                    && mr.DeclaringType.Name == "PhotonNetwork"
                    && prevStr?.Operand is string prefab)
                    result.Add(new InstantiateEntry(type.FullName, m.Name, prefab));
            }
        }
        return result;
    }
```

- [ ] **Step 13: Implement Main — write generated tables to docs/protocol/generated/**

Add to `Program.cs` (top-level type, same file or separate; here as a static entrypoint class):

```csharp
namespace BlackIce.Recon.Catalog;

public static class Program
{
    public static int Main(string[] args)
    {
        // args[0] = game Managed dir, args[1] = output dir
        var managed = args.Length > 0 ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed";
        var outDir = args.Length > 1 ? args[1]
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "protocol", "generated"));
        Directory.CreateDirectory(outDir);

        var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll"));
        var realtime = ModuleDefinition.ReadModule(Path.Combine(managed, "PhotonRealtime.dll"));

        var rpcs = Catalog.ExtractRpcs(game);
        var consts = Catalog.ExtractNamedConstants(realtime,
            new[] { "OperationCode", "EventCode", "ParameterCode", "ErrorCode" });
        var views = Catalog.ExtractSerializeViews(game);
        var insts = Catalog.ExtractInstantiations(game);

        WriteCsv(Path.Combine(outDir, "rpcs.csv"),
            "DeclaringType,Method,Parameters,ReferencesMasterClient",
            rpcs.Select(r => $"{r.DeclaringType},{r.Method},{string.Join('|', r.Parameters)},{r.ReferencesMasterClient}"));
        WriteCsv(Path.Combine(outDir, "photon_constants.csv"),
            "Group,Name,Value",
            consts.Select(c => $"{c.Group},{c.Name},{c.Value}"));
        WriteCsv(Path.Combine(outDir, "serialize_views.csv"),
            "DeclaringType,StreamCallOrder",
            views.Select(v => $"{v.DeclaringType},{string.Join('|', v.StreamCallOrder)}"));
        WriteCsv(Path.Combine(outDir, "instantiations.csv"),
            "DeclaringType,Method,PrefabName",
            insts.Select(i => $"{i.DeclaringType},{i.Method},{i.PrefabName}"));

        Console.WriteLine($"RPCs={rpcs.Count} Constants={consts.Count} SerializeViews={views.Count} Instantiations={insts.Count}");
        Console.WriteLine($"Wrote tables to {outDir}");
        return 0;
    }

    static void WriteCsv(string path, string header, IEnumerable<string> rows)
        => File.WriteAllLines(path, new[] { header }.Concat(rows));
}
```

- [ ] **Step 14: Run the extractor and verify output**

```bash
cd "$REPO/tools"
dotnet run --project BlackIce.Recon.Catalog
ls "$REPO/docs/protocol/generated/"
head -5 "$REPO/docs/protocol/generated/photon_constants.csv"
wc -l "$REPO/docs/protocol/generated/rpcs.csv"
```

Expected: console prints `RPCs=85` (or close), four CSVs exist, `photon_constants.csv`
contains rows like `OperationCode,Authenticate,230`.

- [ ] **Step 15: Run the full test suite**

```bash
dotnet test BlackIce.Recon.Catalog.Tests
```

Expected: all tests PASS.

- [ ] **Step 16: Commit (source + generated tables; the game DLLs stay untracked)**

```bash
cd "$REPO"
git add tools/ docs/protocol/generated/
git status -s   # MUST NOT list any *.dll
git commit -m "feat(recon): Mono.Cecil static catalog extractor + generated tables"
```

---

## Task 5: BepInEx in-process op-logger plugin

**Files:**
- Create: `$REPO/plugins/BlackIce.OpLogger/BlackIce.OpLogger.csproj`
- Create: `$REPO/plugins/BlackIce.OpLogger/Plugin.cs`
- Create: `$REPO/plugins/BlackIce.OpLogger/PhotonPatches.cs`

> The plugin Harmony-patches the Photon client's structured event/response/send entry
> points so it logs decoded operations (post-decryption). Exact method signatures vary by
> Photon version, so Step 1 confirms them against the game's DLLs before patching.

- [ ] **Step 1: Confirm the hook signatures in the game's Photon DLLs**

```bash
cd "$REPO"
ilspycmd "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/Black Ice_Data/Managed/PhotonRealtime.dll" \
  -o decompiled/PhotonRealtime >/dev/null 2>&1
grep -nE "public void OnEvent|public void OnOperationResponse|public (virtual )?bool SendOperation|class LoadBalancingClient|class LoadBalancingPeer" \
  decompiled/PhotonRealtime/*.cs | head -20
```

Expected: confirms the receiving callbacks `OnEvent(EventData)` and
`OnOperationResponse(OperationResponse)` on `LoadBalancingClient`, and the outgoing
`SendOperation` on `LoadBalancingPeer`/`PhotonPeer`. Record exact declaring types and
signatures; the patch targets in Step 4 must match them.

- [ ] **Step 2: Scaffold the plugin project**

```bash
cd "$REPO/plugins"
dotnet new classlib -n BlackIce.OpLogger -o BlackIce.OpLogger --framework netstandard2.0
```

Replace `BlackIce.OpLogger/BlackIce.OpLogger.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>BlackIce.OpLogger</AssemblyName>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <!-- Reference assemblies resolved from the local game/BepInEx install (not committed). -->
    <GameManaged>C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed</GameManaged>
    <BepInExCore>C:\Program Files (x86)\Steam\steamapps\common\Black Ice\BepInEx\core</BepInExCore>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BepInEx"><HintPath>$(BepInExCore)\BepInEx.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="0Harmony"><HintPath>$(BepInExCore)\0Harmony.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="PhotonRealtime"><HintPath>$(GameManaged)\PhotonRealtime.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Photon3Unity3D"><HintPath>$(GameManaged)\Photon3Unity3D.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="PhotonUnityNetworking"><HintPath>$(GameManaged)\PhotonUnityNetworking.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the plugin entrypoint**

Create `BlackIce.OpLogger/Plugin.cs`:

```csharp
using System;
using System.IO;
using BepInEx;
using HarmonyLib;

namespace BlackIce.OpLogger;

[BepInPlugin("blackice.oplogger", "BlackIce Op Logger", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static StreamWriter Log = null!;

    private void Awake()
    {
        var path = Path.Combine(Paths.BepInExRootPath, "oplog.jsonl");
        Log = new StreamWriter(path, append: true) { AutoFlush = true };
        Write("session", new { note = "op-logger started", utc = DateTime.UtcNow.ToString("o") });
        new Harmony("blackice.oplogger").PatchAll();
        Logger.LogInfo($"BlackIce op-logger active -> {path}");
    }

    /// <summary>Append one structured record as a single JSON line.</summary>
    internal static void Write(string kind, object payload)
    {
        var json = SimpleJson.Serialize(new { t = DateTime.UtcNow.ToString("o"), kind, payload });
        lock (Log) Log.WriteLine(json);
    }
}
```

- [ ] **Step 4: Write the Harmony patches**

Create `BlackIce.OpLogger/PhotonPatches.cs` (adjust the `[HarmonyPatch]` targets to the
exact types/signatures confirmed in Step 1):

```csharp
using System.Collections.Generic;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Realtime;

namespace BlackIce.OpLogger;

/// <summary>Logs every incoming event (decoded, post-decryption).</summary>
[HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnEvent))]
internal static class OnEventPatch
{
    static void Prefix(EventData photonEvent) =>
        Plugin.Write("event", new { code = photonEvent.Code, parameters = Describe(photonEvent.Parameters) });

    internal static Dictionary<string, object> Describe(Dictionary<byte, object> p)
    {
        var d = new Dictionary<string, object>();
        if (p != null) foreach (var kv in p) d[kv.Key.ToString()] = kv.Value?.ToString() ?? "null";
        return d;
    }
}

/// <summary>Logs every operation response from the server.</summary>
[HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnOperationResponse))]
internal static class OnOperationResponsePatch
{
    static void Prefix(OperationResponse operationResponse) =>
        Plugin.Write("response", new {
            code = operationResponse.OperationCode,
            returnCode = operationResponse.ReturnCode,
            debug = operationResponse.DebugMessage,
            parameters = OnEventPatch.Describe(operationResponse.Parameters)
        });
}
```

> If Step 1 shows outgoing operations are easiest to capture on `LoadBalancingPeer.SendOperation`,
> add a third patch class targeting it with a `Prefix(byte operationCode, Dictionary<byte,object> operationParameters, SendOptions sendOptions)`
> logging `kind="send"`. Keep the parameter list identical to the confirmed signature.

- [ ] **Step 5: Add a minimal JSON serializer (avoid extra dependencies in the plugin)**

Create `BlackIce.OpLogger/SimpleJson.cs`:

```csharp
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BlackIce.OpLogger;

/// <summary>Tiny reflection-based JSON writer — enough for flat log records.</summary>
internal static class SimpleJson
{
    public static string Serialize(object o)
    {
        var sb = new StringBuilder();
        WriteValue(sb, o);
        return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case float or double:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case IDictionary dict: WriteDict(sb, dict); break;
            default: WriteObject(sb, v); break;
        }
    }

    static void WriteDict(StringBuilder sb, IDictionary d)
    {
        sb.Append('{'); bool first = true;
        foreach (DictionaryEntry e in d)
        {
            if (!first) sb.Append(','); first = false;
            WriteString(sb, e.Key?.ToString() ?? "null"); sb.Append(':'); WriteValue(sb, e.Value);
        }
        sb.Append('}');
    }

    static void WriteObject(StringBuilder sb, object v)
    {
        sb.Append('{'); bool first = true;
        foreach (PropertyInfo p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!first) sb.Append(','); first = false;
            WriteString(sb, p.Name); sb.Append(':'); WriteValue(sb, p.GetValue(v));
        }
        sb.Append('}');
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        sb.Append('"');
    }
}
```

- [ ] **Step 6: Build and deploy the plugin**

```bash
cd "$REPO/plugins/BlackIce.OpLogger"
dotnet build -c Release
cp bin/Release/netstandard2.0/BlackIce.OpLogger.dll \
   "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/plugins/"
ls "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/plugins/"
```

Expected: build succeeds; `BlackIce.OpLogger.dll` is in the game's `BepInEx/plugins`.

- [ ] **Step 7: Run the game and verify the op-log populates**

Launch the game, go online (reach the main menu / attempt matchmaking so PUN connects),
then quit. Then:

```bash
LOG="/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/oplog.jsonl"
wc -l "$LOG"
grep -m1 '"kind":"response"' "$LOG"
grep -oE '"code":[0-9]+' "$LOG" | sort | uniq -c | sort -rn | head
```

Expected: many JSONL lines; at least one `response`/`event` record; a histogram of opcodes.
This is live, decoded protocol evidence.

- [ ] **Step 8: Commit the plugin source (not the built DLL)**

```bash
cd "$REPO"
git add plugins/
git status -s    # MUST NOT list bin/ or obj/ or any *.dll
git commit -m "feat(recon): BepInEx op-logger plugin for decoded Photon ops"
```

---

## Task 6: Wire capture (transport framing)

**Files:**
- Create: `$REPO/tools/capture/capture.ps1`

- [ ] **Step 1: Confirm tshark is available**

```bash
which tshark || echo "MISSING"
```

If MISSING: install Wireshark (`winget install WiresharkFoundation.Wireshark`) or have the
operator install it, then re-check. tshark is required for this task.

- [ ] **Step 2: Write the capture wrapper**

Create `tools/capture/capture.ps1`:

```powershell
# Captures UDP traffic for the Black Ice process to a timestamped pcapng.
# Usage: pwsh tools/capture/capture.ps1 -Seconds 120
param([int]$Seconds = 120, [string]$OutDir = "$PSScriptRoot\..\..\captures")
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $OutDir "blackice-$stamp.pcapng"
# Photon default ports: Name 5058/27000(udp), Master/Game in 5055-5056/27000+; capture all UDP, filter later.
Write-Host "Capturing UDP for $Seconds s -> $out"
tshark -a "duration:$Seconds" -f "udp" -w $out
Write-Host "Saved $out"
```

- [ ] **Step 3: Capture a connect session**

Start the capture, launch the game, connect online for ~1 minute, quit:

```bash
pwsh "$REPO/tools/capture/capture.ps1" -Seconds 120
ls -la "$REPO/captures/"
```

Expected: a `blackice-*.pcapng` file (non-empty).

- [ ] **Step 4: Summarize the transport in the capture**

```bash
PCAP=$(ls -t "$REPO/captures/"*.pcapng | head -1)
tshark -r "$PCAP" -q -z conv,udp 2>/dev/null | head -20
```

Expected: a UDP conversation list showing the Photon server endpoints and packet/byte
counts — confirms which remote IPs/ports the client talks to (Name/Master/Game servers).
Record these endpoints; they inform `docs/protocol/05-transport.md`.

- [ ] **Step 5: Commit the capture script and a tiny sanitized sample**

```bash
cd "$REPO"
# Keep one short, sanitized sample as a fixture; the full corpus stays gitignored.
PCAP=$(ls -t captures/*.pcapng | head -1)
tshark -r "$PCAP" -c 200 -w captures/sample-connect-200pkts.pcapng
git add tools/capture/capture.ps1
git add -f captures/sample-connect-200pkts.pcapng   # force-add the one allowed fixture
git commit -m "feat(recon): tshark capture wrapper + sample connect fixture"
```

> Only the explicitly force-added sample fixture is committed; `captures/` stays gitignored.

---

## Task 7: Capture correlation parser

**Files:**
- Create: `$REPO/tools/capture/parse_oplog.py`
- Test: `$REPO/tools/capture/test_parse_oplog.py`

> Turns the op-logger JSONL into a readable, opcode-labelled timeline, joining numeric
> opcodes to the names from `docs/protocol/generated/photon_constants.csv`.

- [ ] **Step 1: Write the failing test**

Create `tools/capture/test_parse_oplog.py`:

```python
import json
from parse_oplog import label_records

def test_labels_opcodes_from_constant_map():
    records = [
        {"t": "2026-05-29T00:00:00Z", "kind": "response", "payload": {"code": 230}},
        {"t": "2026-05-29T00:00:01Z", "kind": "event",    "payload": {"code": 255}},
    ]
    op_names = {"OperationCode": {230: "Authenticate"}, "EventCode": {255: "Join"}}
    out = label_records(records, op_names)
    assert out[0]["label"] == "Authenticate"   # response -> OperationCode
    assert out[1]["label"] == "Join"           # event -> EventCode
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd "$REPO/tools/capture"
python -m pytest test_parse_oplog.py -v
```

Expected: FAIL — `parse_oplog` / `label_records` missing.

- [ ] **Step 3: Implement the parser**

Create `tools/capture/parse_oplog.py`:

```python
"""Label and summarize the BepInEx op-logger JSONL into a readable timeline."""
import csv
import json
import sys
from pathlib import Path


def load_constant_map(csv_path: Path) -> dict:
    """photon_constants.csv (Group,Name,Value) -> {Group: {int_value: Name}}."""
    out: dict[str, dict[int, str]] = {}
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            try:
                value = int(row["Value"])
            except (ValueError, KeyError):
                continue
            out.setdefault(row["Group"], {})[value] = row["Name"]
    return out


def label_records(records: list[dict], op_names: dict) -> list[dict]:
    """Attach a human label to each record by joining its numeric code to the right group."""
    group_for_kind = {"response": "OperationCode", "send": "OperationCode", "event": "EventCode"}
    labelled = []
    for r in records:
        group = group_for_kind.get(r.get("kind"), "")
        code = (r.get("payload") or {}).get("code")
        label = op_names.get(group, {}).get(code, f"{group}:{code}")
        labelled.append({**r, "label": label})
    return labelled


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: parse_oplog.py <oplog.jsonl> <photon_constants.csv>")
        return 2
    records = [json.loads(line) for line in Path(sys.argv[1]).read_text().splitlines() if line.strip()]
    op_names = load_constant_map(Path(sys.argv[2]))
    for r in label_records(records, op_names):
        print(f"{r['t']}  {r['kind']:<9} {r['label']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd "$REPO/tools/capture"
python -m pytest test_parse_oplog.py -v
```

Expected: PASS.

- [ ] **Step 5: Run the parser against the real op-log**

```bash
cd "$REPO/tools/capture"
python parse_oplog.py \
  "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/oplog.jsonl" \
  "$REPO/docs/protocol/generated/photon_constants.csv" | head -40
```

Expected: a timeline of named operations (e.g. `... response   Authenticate`).

- [ ] **Step 6: Commit**

```bash
cd "$REPO"
git add tools/capture/parse_oplog.py tools/capture/test_parse_oplog.py
git commit -m "feat(recon): op-log correlation parser with opcode labelling"
```

---

## Task 8: Author the protocol specification documents

**Files:**
- Create: `$REPO/docs/protocol/00-overview.md` … `05-transport.md`

> Synthesize the evidence (generated tables, op-log timeline, pcap summary) into the
> human-readable spec. Each doc is tables-first. No invented values — every code/field
> traces back to a generated table or a capture.

- [ ] **Step 1: Write 00-overview.md**

Summarize: engine (Unity/Mono), networking (Photon PUN), the Name→Master→Game topology,
master-client authority, and a links table to the other docs and the generated CSVs.

- [ ] **Step 2: Write 01-connection-flow.md**

From the op-log timeline (Task 7) and pcap endpoints (Task 6), document the ordered
handshake: Name Server connect → `Authenticate` → region/Master Server address → Master
connect → matchmaking ops → Game Server address → Game connect → `JoinRoom`/`CreateRoom`.
Include a sequence list with the observed operation codes and return codes.

- [ ] **Step 3: Write 02-operations.md**

Embed/reference `generated/photon_constants.csv`: tables for OperationCode, EventCode,
ParameterCode, ErrorCode (name → value), annotated with which were observed live in the
op-log and their direction.

- [ ] **Step 4: Write 03-rpc-catalog.md**

Embed/reference `generated/rpcs.csv`: the RPC table (declaring type, method, parameters,
`ReferencesMasterClient` authority hint). Group by subsystem (combat, enemies, inventory,
buildings) and flag the authority-sensitive ones (the Phase 3/4 anti-cheat targets).

- [ ] **Step 5: Write 04-serialization.md**

Document GpBinary type tags (from Photon3Unity3D `GpType`/protocol) and the
`OnPhotonSerializeView` payload call order from `generated/serialize_views.csv` for the 6
continuous-sync components.

- [ ] **Step 6: Write 05-transport.md**

From the pcap (Task 6): Photon reliable-UDP framing — the command/channel model, sequencing,
fragmentation/reassembly, and the ack/reliability scheme observed. Note the server endpoints
and ports.

- [ ] **Step 7: Cross-check and commit**

Verify every doc's codes/fields trace to a generated CSV or a capture line (no invented
values). Then:

```bash
cd "$REPO"
git add docs/protocol/
git commit -m "docs(protocol): Phase 0 protocol specification (connect, ops, RPCs, serialization, transport)"
```

---

## Self-Review

**Spec coverage:**
- §2 deliverables → protocol docs (Task 8), tooling (Tasks 4,5,7), capture corpus (Tasks 5,6), toolkit doc (Tasks 2,3). ✓
- §3 repo/OSS hygiene → Task 1 (GPLv3, README/NOTICE, gitignore guard on every commit). ✓
- §4a static catalog → Task 4. §4b op logger → Task 5. §4c wire capture → Task 6. ✓
- §5 spec output format (01–05 + generated/) → Task 8 + Task 4 Step 13. ✓
- §6 toolkit (dnSpyEx, BepInEx, gdb, Frida, Wireshark) → Tasks 2,3,6. ✓
- §7 execution order → Tasks ordered identically. ✓

**Placeholder scan:** No "TBD/TODO" steps; license resolved to GPLv3; every code step shows
complete code; setup steps give exact commands + expected output. The two intentionally
adaptive points (op-logger hook signatures in Task 5, debugger-enable method in Task 3) are
gated by an explicit confirmation step with a fallback, not left vague.

**Type consistency:** `Catalog.ExtractRpcs/ExtractNamedConstants/ExtractSerializeViews/
ExtractInstantiations` defined in Task 4 and used by `Program.Main` with matching signatures.
Record types in `Records.cs` match their construction sites. `label_records(records, op_names)`
signature matches its test and `main()` call. `SimpleJson.Serialize` defined and used by `Plugin.Write`.

**Adaptive-point note:** Tasks 3 and 5 contain steps whose exact targets are confirmed
against the live binaries before code is written — by design for reverse engineering, where
the ground truth is the shipped DLL, not documentation.
```
