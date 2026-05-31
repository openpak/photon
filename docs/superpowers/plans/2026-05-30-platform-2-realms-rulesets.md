# Server Platform SP2 — Realms & Rulesets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single hardcoded test room with many DB-backed realms, each with its own native-knob ruleset, seeded from config, advertised in the in-game browser, and joins routed with the realm's rules applied.

**Architecture:** A `Realm` EF Core entity + `RealmService` (in `BlackIce.Server.Data`) own realm definitions; the in-memory `RoomRegistry` owns live occupancy. The Master builds the lobby GameList from enabled/visible realms (live player counts from the registry); the Game applies a realm's ruleset when a player enters its room and rejects unknown/disabled realms and password mismatches.

**Tech Stack:** .NET 8, EF Core 8 (SQLite/MySQL), xUnit with in-memory SQLite (existing `TestDb`).

**Environment:** branch `platform-sp2` (already created). `$REPO` = repo root. Run from `$REPO/server`. **Stop any running server before building.** Existing patterns: SP1's `AccountService`/`TestDb`/`DatabaseOptions`/`ServerConfig`.

---

## File Structure

```
server/BlackIce.Server.Data/
  Realm.cs                 # Realm entity
  RealmService.cs          # seed-if-empty, list, get, upsert, delete
  BlackIceDbContext.cs     # + DbSet<Realm>
  RealmServiceTests.cs     # (in Data.Tests)
server/BlackIce.Server.LoadBalancing/
  RoomRegistry.cs          # + Find(name)
  MasterServerHandler.cs   # GameList from realms
  GameServerHandler.cs     # apply realm ruleset on join
server/BlackIce.Server.Host/
  ServerConfig.cs          # + List<Realm> Realms (defaults); drop TestRoomName
  Program.cs               # build+seed RealmService, pass to handlers
```

---

## Task 1: Realm entity + DbContext

**Files:** Create `BlackIce.Server.Data/Realm.cs`; Modify `BlackIceDbContext.cs`.

- [ ] **Step 1: Write the entity**

`BlackIce.Server.Data/Realm.cs`:
```csharp
using System.ComponentModel.DataAnnotations;

namespace BlackIce.Server.Data;

/// <summary>A persistent realm definition: a named room plus its native-knob ruleset.</summary>
public class Realm
{
    [Key] public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Pvp { get; set; }
    public int HackDifficultyIncrease { get; set; }
    public string Password { get; set; } = "";       // "" = open
    public int MaxPlayers { get; set; } = 8;
    public bool IsVisible { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public string ExtraJson { get; set; } = "{}";     // future server-enforced rules (stored, not enforced)
}
```

- [ ] **Step 2: Add the DbSet**

In `BlackIceDbContext.cs`, add:
```csharp
    public DbSet<Realm> Realms => Set<Realm>();
```

- [ ] **Step 3: Build**

```bash
cd "$REPO/server" && dotnet build BlackIce.Server.Data 2>&1 | tail -3
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(data): Realm entity + DbSet"
```

---

## Task 2: RealmService

**Files:** Create `RealmService.cs`; Test `RealmServiceTests.cs`.

- [ ] **Step 1: Failing tests**

`BlackIce.Server.Data.Tests/RealmServiceTests.cs`:
```csharp
using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class RealmServiceTests
{
    [Fact]
    public void SeedDefaults_inserts_only_when_table_empty()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.SeedDefaults(new[] { new Realm { Name = "PvE" }, new Realm { Name = "PvP", Pvp = true } });
        svc.SeedDefaults(new[] { new Realm { Name = "Extra" } });   // table not empty -> no-op
        Assert.Equal(2, svc.ListEnabled().Count);
        Assert.Null(svc.Get("Extra"));
    }

    [Fact]
    public void ListVisible_excludes_hidden_and_disabled()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.Upsert(new Realm { Name = "Shown", IsVisible = true, IsEnabled = true });
        svc.Upsert(new Realm { Name = "Hidden", IsVisible = false, IsEnabled = true });
        svc.Upsert(new Realm { Name = "Off", IsVisible = true, IsEnabled = false });
        var visible = svc.ListVisible();
        Assert.Single(visible);
        Assert.Equal("Shown", visible[0].Name);
    }

    [Fact]
    public void Upsert_updates_existing_and_delete_removes()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.Upsert(new Realm { Name = "R", Pvp = false });
        svc.Upsert(new Realm { Name = "R", Pvp = true });
        Assert.True(svc.Get("R")!.Pvp);
        Assert.True(svc.Delete("R"));
        Assert.Null(svc.Get("R"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter RealmService 2>&1 | tail -5
```
Expected: FAIL — `RealmService` not defined.

- [ ] **Step 3: Implement**

`BlackIce.Server.Data/RealmService.cs`:
```csharp
namespace BlackIce.Server.Data;

/// <summary>Owns realm definitions: seeding, listing, and CRUD.</summary>
public sealed class RealmService
{
    private readonly BlackIceDbContext _db;
    public RealmService(BlackIceDbContext db) => _db = db;

    /// <summary>Inserts the given realms only if no realms exist yet (first-run seeding).</summary>
    public void SeedDefaults(IEnumerable<Realm> defaults)
    {
        if (_db.Realms.Any()) return;
        _db.Realms.AddRange(defaults);
        _db.SaveChanges();
    }

    public IReadOnlyList<Realm> ListEnabled() => _db.Realms.Where(r => r.IsEnabled).ToList();
    public IReadOnlyList<Realm> ListVisible() => _db.Realms.Where(r => r.IsEnabled && r.IsVisible).ToList();
    public Realm? Get(string name) => _db.Realms.FirstOrDefault(r => r.Name == name);

    public Realm Upsert(Realm realm)
    {
        var existing = _db.Realms.FirstOrDefault(r => r.Name == realm.Name);
        if (existing is null) { _db.Realms.Add(realm); }
        else
        {
            existing.DisplayName = realm.DisplayName;
            existing.Pvp = realm.Pvp;
            existing.HackDifficultyIncrease = realm.HackDifficultyIncrease;
            existing.Password = realm.Password;
            existing.MaxPlayers = realm.MaxPlayers;
            existing.IsVisible = realm.IsVisible;
            existing.IsEnabled = realm.IsEnabled;
            existing.ExtraJson = realm.ExtraJson;
        }
        _db.SaveChanges();
        return _db.Realms.First(r => r.Name == realm.Name);
    }

    public bool Delete(string name)
    {
        var r = _db.Realms.FirstOrDefault(x => x.Name == name);
        if (r is null) return false;
        _db.Realms.Remove(r);
        _db.SaveChanges();
        return true;
    }
}
```

- [ ] **Step 4: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(data): RealmService (seed/list/get/upsert/delete)"
```

---

## Task 3: RoomRegistry.Find (live occupancy lookup)

**Files:** Modify `BlackIce.Server.LoadBalancing/RoomRegistry.cs`.

- [ ] **Step 1: Add a non-creating lookup**

In `RoomRegistry`, add (next to `GetOrCreate`):
```csharp
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
```
(`_rooms` is the existing `ConcurrentDictionary<string, Room>`.)

- [ ] **Step 2: Build + commit**

```bash
cd "$REPO/server" && dotnet build BlackIce.Server.LoadBalancing 2>&1 | tail -3
cd "$REPO" && git add server/ && git commit -m "feat(lb): RoomRegistry.Find for live player counts"
```

---

## Task 4: Master advertises realms

**Files:** Modify `MasterServerHandler.cs`; Test `BlackIce.Server.Tests/RealmGameListTests.cs`.

> Replaces the single `_testRoomName` GameList logic with one entry per visible realm.

- [ ] **Step 1: Failing test**

`BlackIce.Server.Tests/RealmGameListTests.cs`:
```csharp
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RealmGameListTests
{
    [Fact]
    public void GameList_lists_each_visible_realm_with_props()
    {
        var realms = TestAccounts.CreateRealms();   // helper (Task 4 Step 3)
        realms.Upsert(new Realm { Name = "PvP Arena", Pvp = true, HackDifficultyIncrease = 3, MaxPlayers = 6 });
        realms.Upsert(new Realm { Name = "Hidden", IsVisible = false });

        var master = new MasterServerHandler("127.0.0.1:5056", "secret", new RoomRegistry(),
                                             realms: realms);
        var ev = master.BuildGameListEvent();
        var rooms = (Dictionary<string, object>)ev.Parameters[222];

        Assert.True(rooms.ContainsKey("PvP Arena"));
        Assert.False(rooms.ContainsKey("Hidden"));
        var props = (Dictionary<object, object>)rooms["PvP Arena"];
        Assert.Equal(true, props["PVP"]);
        Assert.Equal(3, props["HackDifficultyIncrease"]);
        Assert.Equal((byte)6, props[(byte)255]);     // MaxPlayers
    }
}
```

- [ ] **Step 2: Add the test realm helper**

Append to `BlackIce.Server.Tests/TestAccounts.cs`:
```csharp
    public static RealmService CreateRealms()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<BlackIceDbContext>().UseSqlite(conn).Options;
        var ctx = new BlackIceDbContext(options);
        ctx.Database.EnsureCreated();
        return new RealmService(ctx);
    }
```

- [ ] **Step 3: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter GameList_lists_each_visible_realm 2>&1 | tail -5
```
Expected: FAIL — `MasterServerHandler` has no `realms` parameter.

- [ ] **Step 4: Update MasterServerHandler**

Replace the `_testRoomName` field + ctor param with a `RealmService`:
```csharp
    private readonly RealmService? _realms;
    // ...ctor: replace `string? testRoomName = null` with `RealmService? realms = null` and store it.
    public MasterServerHandler(string gameAddress, string secret, RoomRegistry registry,
                               bool allowAnonymousLan = false, AccountService? accounts = null,
                               RealmService? realms = null)
    {
        _gameAddress = gameAddress; _secret = secret; _registry = registry;
        _allowAnonymousLan = allowAnonymousLan; _accounts = accounts; _realms = realms;
    }
```

Rewrite `BuildGameListEvent()`:
```csharp
    public EventData BuildGameListEvent()
    {
        var rooms = new Dictionary<string, object>();
        foreach (var realm in _realms?.ListVisible() ?? new List<Realm>())
        {
            int players = _registry.Find(realm.Name)?.ActorNumbers.Count ?? 0;
            rooms[realm.Name] = new Dictionary<object, object>
            {
                { RoomIsOpen, true },
                { RoomIsVisible, true },
                { RoomPlayerCount, (byte)players },
                { RoomMaxPlayers, (byte)realm.MaxPlayers },
                { "PVP", realm.Pvp },
                { "HackDifficultyIncrease", realm.HackDifficultyIncrease },
                { "Password", realm.Password },
            };
        }
        return new EventData(EvGameList, new() { { PGameListMap, rooms } });
    }
```
Remove the old `_testRoomName` references. Keep the `RoomIs*`/`RoomMaxPlayers` consts.

- [ ] **Step 5: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter GameList_lists_each_visible_realm 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(lb): Master advertises all visible realms in the GameList"
```

---

## Task 5: Game applies the realm ruleset on join

**Files:** Modify `GameServerHandler.cs`; Test `BlackIce.Server.Tests/RealmJoinTests.cs`.

- [ ] **Step 1: Failing tests**

`BlackIce.Server.Tests/RealmJoinTests.cs`:
```csharp
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RealmJoinTests
{
    private static GameServerHandler Make(RealmService realms) =>
        new("secret", new RoomRegistry(), accounts: null, realms: realms);

    [Fact]
    public void EnterRoom_applies_realm_ruleset_to_game_properties()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Hard", Pvp = true, HackDifficultyIncrease = 5, MaxPlayers = 4 });
        var (resp, join) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Hard" } }), null);

        Assert.Equal(0, resp.ReturnCode);
        var props = (Dictionary<object, object>)resp.Parameters[248];   // GameProperties
        Assert.Equal(true, props["PVP"]);
        Assert.Equal(5, props["HackDifficultyIncrease"]);
        Assert.Equal(255, join.Code);
    }

    [Fact]
    public void EnterRoom_rejects_unknown_realm()
    {
        var realms = TestAccounts.CreateRealms();
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Nope" } }), null);
        Assert.NotEqual(0, resp.ReturnCode);
    }

    [Fact]
    public void EnterRoom_rejects_wrong_password()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Locked", Password = "secret123" });
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Locked" } }), "wrong");
        Assert.NotEqual(0, resp.ReturnCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter RealmJoin 2>&1 | tail -5
```
Expected: FAIL — `GameServerHandler` has no `realms` param and `EnterRoom` signature differs.

- [ ] **Step 3: Update GameServerHandler**

Add a `RealmService? _realms` ctor param (after `accounts`), store it. Change `EnterRoom` to take an optional join password and apply the realm:
```csharp
    public (OperationResponse Response, EventData Join) EnterRoom(OperationRequest r, string? joinPassword)
    {
        var name = r.Parameters.TryGetValue(PRoomName, out var n) ? n.ToString()! : "room";
        var realm = _realms?.Get(name);
        if (_realms is not null && (realm is null || !realm.IsEnabled))
            return (new OperationResponse(r.OperationCode, -4, "No such realm", new()), new EventData(EvJoin, new()));
        if (realm is not null && realm.Password.Length > 0 && joinPassword != realm.Password)
            return (new OperationResponse(r.OperationCode, -5, "Wrong password", new()), new EventData(EvJoin, new()));

        var room = _registry.GetOrCreate(name);
        int actor = room.AddActor();

        var gameProps = new Dictionary<object, object>();
        if (realm is not null)
        {
            gameProps["PVP"] = realm.Pvp;
            gameProps["HackDifficultyIncrease"] = realm.HackDifficultyIncrease;
            gameProps["Password"] = realm.Password;
        }

        var response = new OperationResponse(r.OperationCode, 0, null, new()
        {
            { PActorNr, actor },
            { PGameProperties, gameProps },
            { PActorProperties, new Dictionary<byte, object>() },
        });
        var join = new EventData(EvJoin, new()
        {
            { PActorNr, actor },
            { PActorList, room.ActorNumbers.ToArray() },
        });
        return (response, join);
    }
```
Update the `OnOperationRequest` CreateGame/JoinGame branch to pass the join password (the client sends it in a password param; pass `null` if absent — confirm the param key during live test, default to no password check when absent):
```csharp
            case OpCreateGame:
            case OpJoinGame:
                var pwd = request.Parameters.TryGetValue(PRoomName == 255 ? (byte)250 : (byte)0, out _) ? null : null; // placeholder removed below
                var (response, join) = EnterRoom(request, ExtractJoinPassword(request));
                peer.SendResponse(response);
                peer.RaiseEvent(join);
                break;
```
Add a helper (password param key confirmed in the live smoke; default null = no check unless realm locked, which then rejects — acceptable, refined live):
```csharp
    private static string? ExtractJoinPassword(OperationRequest r)
        => r.Parameters.TryGetValue(248, out var gp) && gp is System.Collections.IDictionary d && d.Contains("Password")
            ? d["Password"]?.ToString() : null;
```
(Add `PGameProperties`/`PActorProperties`/`PActorList` consts already exist.)

- [ ] **Step 4: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter RealmJoin 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(lb): Game applies realm ruleset, rejects unknown realm + bad password"
```

---

## Task 6: Host — seed realms from config, wire handlers

**Files:** Modify `ServerConfig.cs`, `Program.cs`.

- [ ] **Step 1: Add default realms to config**

In `ServerConfig.cs`: remove `TestRoomName`; add:
```csharp
    public List<Realm> Realms { get; set; } = new()
    {
        new Realm { Name = "Black Ice — Co-op", DisplayName = "Co-op", Pvp = false, MaxPlayers = 8 },
        new Realm { Name = "Black Ice — PvP", DisplayName = "PvP", Pvp = true, MaxPlayers = 6 },
        new Realm { Name = "Black Ice — Hardcore", DisplayName = "Hardcore", HackDifficultyIncrease = 5, MaxPlayers = 4 },
    };
```
(Add `using BlackIce.Server.Data;` if not present.)

- [ ] **Step 2: Wire RealmService in Program.cs**

Replace the `registry.GetOrCreate(testRoomName)` block with:
```csharp
var realms = new RealmService(config.Database.CreateContext());
realms.SeedDefaults(config.Realms);
var registry = new RoomRegistry();
```
Update the handler constructions:
```csharp
    new UdpListener("MasterServer", 5055, new MasterServerHandler($"{config.AdvertisedHost}:5056", secret, registry, config.AllowAnonymousLan, accounts, realms)),
    new UdpListener("GameServer", 5056, new GameServerHandler(secret, registry, config.AllowAnonymousLan, accounts, realms)),
```

- [ ] **Step 3: Build whole solution**

```bash
cd "$REPO" && dotnet build BlackIce.sln 2>&1 | tail -4
```
Expected: `Build succeeded.` Fix any leftover `testRoomName` references.

- [ ] **Step 4: Full test suite**

```bash
cd "$REPO/server" && dotnet test "$REPO/BlackIce.sln" 2>&1 | grep -E "Passed!|Failed!" | head
```
Expected: all PASS. (The Phase-1 `Master_advertises_test_room_in_gamelist` test is now obsolete — replace/remove it in favor of the realm GameList test.)

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(host): seed realms from config, wire RealmService into handlers"
```

---

## Task 7: Live smoke — realms appear in the browser

- [ ] **Step 1: Run server (fresh DB) and connect**

```bash
cd "$REPO/server/BlackIce.Server.Host/bin/Release/net8.0"
powershell.exe -NoProfile -Command "(Get-NetUDPEndpoint -LocalPort 5058 -ErrorAction SilentlyContinue).OwningProcess | %{ Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }" 2>/dev/null
rm -f blackice.db
dotnet build "$REPO/server/BlackIce.Server.Host" -c Release 2>&1 | grep -E "Build succeeded|error" | head -1
nohup ./BlackIce.Server.Host.exe 127.0.0.1 > /tmp/bi-sp2.log 2>&1 &
sleep 5
powershell.exe -NoProfile -Command "Start-Process -FilePath 'C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice.exe'"
sleep 45
powershell.exe -NoProfile -Command "Get-Process 'Black Ice' -ErrorAction SilentlyContinue | Stop-Process -Force"
```

- [ ] **Step 2: Verify the GameList carried all realms**

```bash
grep -iE "JoinLobby|op 229" /tmp/bi-sp2.log | head
```
Expected: the client requested the lobby (op 229) and got a GameList; the server didn't error serializing it. (The in-game confirmation — three realms listed in the browser — is the manual check by the operator.) Then:
```bash
powershell.exe -NoProfile -Command "(Get-NetUDPEndpoint -LocalPort 5058 -ErrorAction SilentlyContinue).OwningProcess | %{ Stop-Process -Id \$_ -Force }" 2>/dev/null
```

- [ ] **Step 3: Note + commit**

Append a "SP2 verified" line to `server/ORACLE.md` and commit if anything changed.

---

## Self-Review

**Spec coverage:**
- §3 Realm entity (all fields incl ExtraJson) → Task 1. ✓
- §3 RealmService (seed-if-empty, list enabled/visible, get/upsert/delete) → Task 2. ✓
- §3 RoomRegistry live count → Task 3. ✓
- §3/§4 Master GameList from visible realms + live counts → Task 4. ✓
- §3/§4 Game applies ruleset + rejects unknown/disabled + password → Task 5. ✓
- §3 Host seeds from config, drops TestRoomName → Task 6. ✓
- §6 error handling (unknown realm -4, wrong password -5, never crash) → Task 5. ✓
- §7 testing (RealmService, GameList, EnterRoom; suite green; live smoke) → Tasks 2,4,5,7. ✓
- §2 DoD (realms seeded, listed, joinable with rules) → Tasks 6,7. ✓

**Placeholder scan:** Task 5 Step 3 had a placeholder `pwd` line — REMOVE it; use only `ExtractJoinPassword(request)`. The password param key is confirmed in the live smoke (Task 7); until then `ExtractJoinPassword` reads it from the join's GameProperties `"Password"`, returning null if absent (locked realms then reject, which is the safe default). No other TBDs.

**Type consistency:** `RealmService` methods (`SeedDefaults`, `ListEnabled`, `ListVisible`, `Get`, `Upsert`, `Delete`) defined in Task 2, used in Tasks 4–6. `MasterServerHandler` ctor gains trailing `RealmService? realms` (Task 4); `GameServerHandler` ctor gains trailing `RealmService? realms` and `EnterRoom(request, joinPassword)` (Task 5); Host calls match (Task 6). `RoomRegistry.Find` (Task 3) used in Task 4. `Realm` fields consistent throughout. Existing handler params (`accounts`) precede the new `realms` param.

**Note:** Task 6 removes the now-obsolete `Master_advertises_test_room_in_gamelist` test (replaced by `RealmGameListTests`).
