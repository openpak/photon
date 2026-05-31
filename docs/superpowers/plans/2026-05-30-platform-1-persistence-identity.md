# Server Platform SP1 — Persistence & Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a pluggable EF Core data layer (SQLite default, MySQL-swappable) with SteamID-keyed accounts auto-created on first connect, a 4-level permission model, a one-time bootstrap code, and a server console command loop — so the server has persistent identity to build realms and admin commands on.

**Architecture:** A new `BlackIce.Server.Data` project owns the `DbContext`, entities, and an `AccountService` (resolve-or-create, level/ban management, bootstrap). The Name/Master/Game handlers resolve the player's account on Authenticate (identity = SteamID sent by the client mod as the Photon UserId) and reject bans. The Host loads config, creates the schema, prints the bootstrap code, and runs a console command loop alongside the UDP listeners.

**Tech Stack:** .NET 8, EF Core 8 (`Microsoft.EntityFrameworkCore.Sqlite` + `Pomelo.EntityFrameworkCore.MySql`), xUnit with EF Core SQLite in-memory, System.Text.Json config, BepInEx/Steamworks (client mod).

**Deviation from spec (deliberate):** SP1 uses `Database.EnsureCreated()` rather than EF migrations. Multi-provider migrations (SQLite + MySQL) need per-provider migration assemblies — unjustified complexity while SQLite is the only live backend. `EnsureCreated` builds the schema from the model under either provider. Migrations become the upgrade path when MySQL/schema-evolution is actually used.

**Environment:** `$REPO` = `C:\Users\natew\OneDrive\Documentos\blackice-re`; `$GAME` = the Steam install; `$MANAGED` = `$GAME\Black Ice_Data\Managed`. Work on branch `platform-sp1` (created in Task 1). Run Bash-tool commands from `$REPO/server` unless noted. **Stop any running BlackIce.Server.Host before building** (it locks DLLs): `powershell.exe -NoProfile -Command "(Get-NetUDPEndpoint -LocalPort 5058 -ErrorAction SilentlyContinue).OwningProcess | %{ Stop-Process -Id $_ -Force }"`.

---

## File Structure

```
server/
  BlackIce.Server.Data/
    BlackIce.Server.Data.csproj
    PlayerLevel.cs            # enum Player/Mod/Admin/Console
    Entities.cs               # Account, Profile, ServerState
    BlackIceDbContext.cs      # DbSets + model config
    DatabaseOptions.cs        # provider + connection string; context factory
    AccountService.cs         # resolve-or-create, level/ban, bootstrap
    ConsoleCommandProcessor.cs# parse+execute a console admin line
  BlackIce.Server.Data.Tests/
    BlackIce.Server.Data.Tests.csproj
    TestDb.cs                 # SQLite in-memory context helper
    AccountServiceTests.cs
    ConsoleCommandProcessorTests.cs
  BlackIce.Server.LoadBalancing/   # modified: handlers resolve account + reject bans
  BlackIce.Server.Host/            # modified: config, schema create, bootstrap print, console loop
    ServerConfig.cs
plugins/
  BlackIce.Redirect/               # modified: send SteamID as AuthValues.UserId
```

---

## Task 1: Scaffold the data project

- [ ] **Step 1: Branch and projects**

```bash
cd "$REPO" && git checkout -b platform-sp1 && git branch --show-current
cd "$REPO/server"
dotnet new classlib -n BlackIce.Server.Data -o BlackIce.Server.Data --framework net8.0 && rm -f BlackIce.Server.Data/Class1.cs
dotnet new xunit -n BlackIce.Server.Data.Tests -o BlackIce.Server.Data.Tests --framework net8.0 && rm -f BlackIce.Server.Data.Tests/UnitTest1.cs
```

- [ ] **Step 2: Add EF Core packages**

```bash
cd "$REPO/server"
dotnet add BlackIce.Server.Data package Microsoft.EntityFrameworkCore.Sqlite --version 8.0.8
dotnet add BlackIce.Server.Data package Pomelo.EntityFrameworkCore.MySql --version 8.0.2
dotnet add BlackIce.Server.Data.Tests package Microsoft.EntityFrameworkCore.Sqlite --version 8.0.8
```

- [ ] **Step 3: Wire references + solution**

```bash
cd "$REPO/server"
dotnet add BlackIce.Server.Data.Tests reference BlackIce.Server.Data
dotnet add BlackIce.Server.Core reference BlackIce.Server.Data
dotnet sln BlackIce.Server.sln add BlackIce.Server.Data BlackIce.Server.Data.Tests
cd "$REPO" && dotnet sln BlackIce.sln add server/BlackIce.Server.Data/BlackIce.Server.Data.csproj server/BlackIce.Server.Data.Tests/BlackIce.Server.Data.Tests.csproj
dotnet build BlackIce.sln 2>&1 | tail -3
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
cd "$REPO" && git add server/ BlackIce.sln && git status -s | grep -iE '/bin/|/obj/|\.dll$' || echo clean
git commit -m "chore(data): scaffold BlackIce.Server.Data + EF Core packages"
```

---

## Task 2: Permission enum, entities, and DbContext

**Files:** Create `PlayerLevel.cs`, `Entities.cs`, `BlackIceDbContext.cs`; Test `TestDb.cs`, `AccountServiceTests.cs` (first test).

- [ ] **Step 1: Write the enum and entities**

`BlackIce.Server.Data/PlayerLevel.cs`:
```csharp
namespace BlackIce.Server.Data;

/// <summary>Account permission tiers. Higher value = more authority.</summary>
public enum PlayerLevel { Player = 0, Mod = 1, Admin = 2, Console = 3 }
```

`BlackIce.Server.Data/Entities.cs`:
```csharp
using System.ComponentModel.DataAnnotations;

namespace BlackIce.Server.Data;

/// <summary>A persistent player identity, keyed by SteamID, auto-created on first connect.</summary>
public class Account
{
    [Key] public string SteamId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PlayerLevel Level { get; set; } = PlayerLevel.Player;
    public bool IsBanned { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public Profile Profile { get; set; } = new();
}

/// <summary>Per-account profile data (placeholders now, room to grow).</summary>
public class Profile
{
    [Key] public string SteamId { get; set; } = "";
    public long PlaytimeSeconds { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Single-row server state, e.g. the one-time bootstrap code.</summary>
public class ServerState
{
    [Key] public int Id { get; set; } = 1;
    public string? BootstrapCode { get; set; }
    public bool BootstrapClaimed { get; set; }
}
```

- [ ] **Step 2: Write the DbContext**

`BlackIce.Server.Data/BlackIceDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

public class BlackIceDbContext : DbContext
{
    public BlackIceDbContext(DbContextOptions<BlackIceDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ServerState> ServerState => Set<ServerState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>().HasOne(a => a.Profile).WithOne()
            .HasForeignKey<Profile>(p => p.SteamId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Account>().Property(a => a.Level).HasConversion<int>();
    }
}
```

- [ ] **Step 3: Write the in-memory test helper + first failing test**

`BlackIce.Server.Data.Tests/TestDb.cs`:
```csharp
using BlackIce.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data.Tests;

/// <summary>Creates an isolated SQLite in-memory BlackIceDbContext (connection kept open per instance).</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public BlackIceDbContext Context { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<BlackIceDbContext>().UseSqlite(_conn).Options;
        Context = new BlackIceDbContext(options);
        Context.Database.EnsureCreated();
    }

    public BlackIceDbContext NewContext()   // a fresh context over the same DB (simulates a new request)
    {
        var options = new DbContextOptionsBuilder<BlackIceDbContext>().UseSqlite(_conn).Options;
        return new BlackIceDbContext(options);
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
```

`BlackIce.Server.Data.Tests/AccountServiceTests.cs`:
```csharp
using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class AccountServiceTests
{
    [Fact]
    public void Schema_supports_account_with_profile()
    {
        using var db = new TestDb();
        db.Context.Accounts.Add(new Account { SteamId = "76561198000000001", DisplayName = "Nate" });
        db.Context.SaveChanges();

        using var read = db.NewContext();
        var acct = read.Accounts.Find("76561198000000001");
        Assert.NotNull(acct);
        Assert.Equal(PlayerLevel.Player, acct!.Level);
        Assert.False(acct.IsBanned);
    }
}
```

- [ ] **Step 4: Run — should pass (schema round-trips)**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter Schema_supports_account_with_profile 2>&1 | tail -4
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(data): PlayerLevel, Account/Profile/ServerState entities, DbContext"
```

---

## Task 3: Database options + provider selection

**Files:** Create `DatabaseOptions.cs`.

- [ ] **Step 1: Implement the provider factory**

`BlackIce.Server.Data/DatabaseOptions.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "Sqlite";              // "Sqlite" | "MySql"
    public string ConnectionString { get; set; } = "Data Source=blackice.db";

    /// <summary>Builds a context configured for the selected provider and ensures the schema exists.</summary>
    public BlackIceDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<BlackIceDbContext>();
        switch (Provider.ToLowerInvariant())
        {
            case "mysql":
                builder.UseMySql(ConnectionString, ServerVersion.AutoDetect(ConnectionString));
                break;
            case "sqlite":
            default:
                builder.UseSqlite(ConnectionString);
                break;
        }
        var ctx = new BlackIceDbContext(builder.Options);
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
```

- [ ] **Step 2: Build to verify both providers compile**

```bash
cd "$REPO/server" && dotnet build BlackIce.Server.Data 2>&1 | tail -3
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(data): config-driven SQLite/MySQL provider selection"
```

---

## Task 4: AccountService — resolve-or-create

**Files:** Create `AccountService.cs`; extend `AccountServiceTests.cs`.

- [ ] **Step 1: Failing tests**

Append to `AccountServiceTests.cs`:
```csharp
    [Fact]
    public void First_connect_creates_player_account_and_profile()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var acct = svc.ResolveOrCreate("76561198000000002", "Runner");

        Assert.Equal(PlayerLevel.Player, acct.Level);
        Assert.Equal("Runner", acct.DisplayName);
        Assert.NotNull(db.NewContext().Profiles.Find("76561198000000002"));
    }

    [Fact]
    public void Second_connect_updates_lastseen_without_duplicating()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var first = svc.ResolveOrCreate("76561198000000003", "A");
        var firstSeen = first.LastSeenUtc;
        System.Threading.Thread.Sleep(5);
        var again = svc.ResolveOrCreate("76561198000000003", "A-renamed");

        Assert.Equal(1, db.NewContext().Accounts.Count());
        Assert.True(again.LastSeenUtc >= firstSeen);
        Assert.Equal("A-renamed", again.DisplayName);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter First_connect_creates_player_account 2>&1 | tail -5
```
Expected: FAIL — `AccountService` not defined.

- [ ] **Step 3: Implement**

`BlackIce.Server.Data/AccountService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

/// <summary>The single entry point for account identity, permissions, and bootstrap state.</summary>
public sealed class AccountService
{
    private readonly BlackIceDbContext _db;
    public AccountService(BlackIceDbContext db) => _db = db;

    /// <summary>Finds the account for a SteamID, creating it (+ profile) at level Player on first contact.</summary>
    public Account ResolveOrCreate(string steamId, string displayName)
    {
        var acct = _db.Accounts.Include(a => a.Profile).FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null)
        {
            acct = new Account
            {
                SteamId = steamId,
                DisplayName = displayName,
                Profile = new Profile { SteamId = steamId },
            };
            _db.Accounts.Add(acct);
        }
        else
        {
            acct.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(displayName)) acct.DisplayName = displayName;
        }
        _db.SaveChanges();
        return acct;
    }

    public Account? Find(string steamId) => _db.Accounts.Include(a => a.Profile).FirstOrDefault(a => a.SteamId == steamId);
}
```

- [ ] **Step 4: Run to verify pass**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests 2>&1 | tail -4
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(data): AccountService.ResolveOrCreate (auto-create on first connect)"
```

---

## Task 5: Level & ban management

**Files:** extend `AccountService.cs`, `AccountServiceTests.cs`.

- [ ] **Step 1: Failing tests**

Append to `AccountServiceTests.cs`:
```csharp
    [Fact]
    public void SetLevel_changes_permission_tier()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s1", "x");
        Assert.True(svc.SetLevel("s1", PlayerLevel.Admin));
        Assert.Equal(PlayerLevel.Admin, svc.Find("s1")!.Level);
        Assert.False(svc.SetLevel("does-not-exist", PlayerLevel.Mod));
    }

    [Fact]
    public void Ban_and_unban_toggle_flag()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s2", "x");
        Assert.True(svc.SetBanned("s2", true));
        Assert.True(svc.Find("s2")!.IsBanned);
        svc.SetBanned("s2", false);
        Assert.False(svc.Find("s2")!.IsBanned);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter "SetLevel_changes_permission_tier|Ban_and_unban" 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement**

Add to `AccountService`:
```csharp
    public bool SetLevel(string steamId, PlayerLevel level)
    {
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return false;
        acct.Level = level;
        _db.SaveChanges();
        return true;
    }

    public bool SetBanned(string steamId, bool banned)
    {
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return false;
        acct.IsBanned = banned;
        _db.SaveChanges();
        return true;
    }

    public IReadOnlyList<Account> All() => _db.Accounts.OrderByDescending(a => a.Level).ToList();
}
```
(Remove the old closing brace of the class so `All()` is inside it.)

- [ ] **Step 4: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(data): account level + ban management"
```

---

## Task 6: One-time bootstrap code

**Files:** extend `AccountService.cs`, `AccountServiceTests.cs`.

- [ ] **Step 1: Failing tests**

Append to `AccountServiceTests.cs`:
```csharp
    [Fact]
    public void Bootstrap_code_is_generated_once_and_persists()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var code = svc.EnsureBootstrapCode();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Equal(code, new AccountService(db.NewContext()).EnsureBootstrapCode()); // stable
    }

    [Fact]
    public void Claiming_code_promotes_to_console_once()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("owner", "Owner");
        var code = svc.EnsureBootstrapCode();

        Assert.True(svc.ClaimBootstrap("owner", code));
        Assert.Equal(PlayerLevel.Console, svc.Find("owner")!.Level);
        Assert.False(svc.ClaimBootstrap("owner", code));   // one-time
        Assert.False(svc.ClaimBootstrap("owner", "wrong")); // bad code
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter "Bootstrap" 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement**

Add to `AccountService` (before the final `}`):
```csharp
    /// <summary>Returns the one-time bootstrap code, generating and persisting it on first call.</summary>
    public string EnsureBootstrapCode()
    {
        var state = _db.ServerState.Find(1);
        if (state is null) { state = new ServerState { Id = 1 }; _db.ServerState.Add(state); }
        if (string.IsNullOrEmpty(state.BootstrapCode))
            state.BootstrapCode = System.Security.Cryptography.RandomNumberGenerator.GetHexString(10).ToUpperInvariant();
        _db.SaveChanges();
        return state.BootstrapCode!;
    }

    /// <summary>Redeems the bootstrap code once, promoting the account to Console.</summary>
    public bool ClaimBootstrap(string steamId, string code)
    {
        var state = _db.ServerState.Find(1);
        if (state is null || state.BootstrapClaimed || string.IsNullOrEmpty(state.BootstrapCode)) return false;
        if (!string.Equals(state.BootstrapCode, code, StringComparison.OrdinalIgnoreCase)) return false;
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return false;
        acct.Level = PlayerLevel.Console;
        state.BootstrapClaimed = true;
        _db.SaveChanges();
        return true;
    }
```

- [ ] **Step 4: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(data): one-time bootstrap code + claim"
```

---

## Task 7: Console command processor

**Files:** Create `ConsoleCommandProcessor.cs`, `ConsoleCommandProcessorTests.cs`.

- [ ] **Step 1: Failing tests**

`BlackIce.Server.Data.Tests/ConsoleCommandProcessorTests.cs`:
```csharp
using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class ConsoleCommandProcessorTests
{
    [Fact]
    public void Promote_sets_level()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s1", "x");
        var proc = new ConsoleCommandProcessor(svc);

        var output = proc.Execute("promote s1 2");
        Assert.Contains("Admin", output);
        Assert.Equal(PlayerLevel.Admin, svc.Find("s1")!.Level);
    }

    [Fact]
    public void Ban_marks_account()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s2", "x");
        var proc = new ConsoleCommandProcessor(svc);
        proc.Execute("ban s2");
        Assert.True(svc.Find("s2")!.IsBanned);
    }

    [Fact]
    public void Unknown_command_returns_help_hint()
    {
        using var db = new TestDb();
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context));
        Assert.Contains("help", proc.Execute("frobnicate").ToLowerInvariant());
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests --filter ConsoleCommandProcessor 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement**

`BlackIce.Server.Data/ConsoleCommandProcessor.cs`:
```csharp
namespace BlackIce.Server.Data;

/// <summary>Parses and executes a single server-console admin line, returning text output.</summary>
public sealed class ConsoleCommandProcessor
{
    private readonly AccountService _accounts;
    public ConsoleCommandProcessor(AccountService accounts) => _accounts = accounts;

    public string Execute(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        switch (parts[0].ToLowerInvariant())
        {
            case "promote" or "demote" when parts.Length == 3 && int.TryParse(parts[2], out var lvl) && lvl is >= 0 and <= 3:
                return _accounts.SetLevel(parts[1], (PlayerLevel)lvl)
                    ? $"{parts[1]} -> {(PlayerLevel)lvl}" : $"no such account: {parts[1]}";
            case "ban" when parts.Length == 2:
                return _accounts.SetBanned(parts[1], true) ? $"banned {parts[1]}" : $"no such account: {parts[1]}";
            case "unban" when parts.Length == 2:
                return _accounts.SetBanned(parts[1], false) ? $"unbanned {parts[1]}" : $"no such account: {parts[1]}";
            case "list":
                return string.Join('\n', _accounts.All().Select(a => $"{a.SteamId} {a.DisplayName} {a.Level}{(a.IsBanned ? " [BANNED]" : "")}"));
            case "code":
                return $"bootstrap code: {_accounts.EnsureBootstrapCode()}";
            case "help":
                return Help;
            default:
                return $"unknown command '{parts[0]}'. type 'help'.";
        }
    }

    public const string Help =
        "commands: promote <steamId> <0-3> | demote <steamId> <0-3> | ban <steamId> | unban <steamId> | list | code | help";
}
```

- [ ] **Step 4: Run + commit**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Data.Tests 2>&1 | tail -4
cd "$REPO" && git add server/ && git commit -m "feat(data): console command processor (promote/ban/list/code)"
```

---

## Task 8: Identity in the auth handlers + token carries SteamID

**Files:** Modify `BlackIce.Server.LoadBalancing/AuthToken.cs`, `NameServerHandler.cs`, `MasterServerHandler.cs`, `GameServerHandler.cs`; Test `BlackIce.Server.Tests/IdentityTests.cs`.

> The client mod sends the SteamID as the Photon UserId → it arrives in the Authenticate
> request as parameter 225 (`ParameterCode.UserId`). The Name Server resolves the account and
> mints a token over the SteamID; Master/Game validate the token to recover the SteamID and
> reject bans. `AccountService` is passed into each handler.

- [ ] **Step 1: Failing test — banned account is refused**

`BlackIce.Server.Tests/IdentityTests.cs`:
```csharp
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.Data.Tests;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class IdentityTests
{
    const byte PUserId = 225;

    [Fact]
    public void NameServer_creates_account_and_returns_token_for_steamid()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var ns = new NameServerHandler("127.0.0.1:5055", "secret", svc);

        var resp = ns.Authenticate(new OperationRequest(230, new() { { PUserId, "76561198000000009" } }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.NotNull(svc.Find("76561198000000009"));        // account auto-created
        Assert.True(resp.Parameters.ContainsKey(221));         // token minted
    }

    [Fact]
    public void NameServer_refuses_banned_account()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("banned-id", "x");
        svc.SetBanned("banned-id", true);
        var ns = new NameServerHandler("127.0.0.1:5055", "secret", svc);

        var resp = ns.Authenticate(new OperationRequest(230, new() { { PUserId, "banned-id" } }));
        Assert.NotEqual(0, resp.ReturnCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter IdentityTests 2>&1 | tail -5
```
Expected: FAIL — `NameServerHandler` ctor has no `AccountService` parameter.

- [ ] **Step 3: Update AuthToken to carry the SteamID**

The token already encodes a userId string (`AuthToken.Mint(userId, secret)`); pass the SteamID as that userId. No signature change needed — `TryValidate` already returns it as `userId`. (No code change if SteamID is what we mint over; this step is a no-op verification that `AuthToken.Mint(steamId, secret)` / `TryValidate(... out steamId)` round-trips. Confirm by reading `AuthToken.cs`.)

- [ ] **Step 4: Add AccountService + identity to NameServerHandler**

Modify `NameServerHandler.cs`:
```csharp
    private const byte PUserId = 225;
    private readonly AccountService _accounts;

    public NameServerHandler(string masterAddress, string secret, AccountService accounts)
    {
        _masterAddress = masterAddress;
        _secret = secret;
        _accounts = accounts;
    }

    public OperationResponse Authenticate(OperationRequest request)
    {
        // Identity = SteamID sent by the client mod as the Photon UserId (225); else a minted id.
        var steamId = request.Parameters.TryGetValue(PUserId, out var u) && u is string s && s.Length > 0
            ? s : Guid.NewGuid().ToString();
        var account = _accounts.ResolveOrCreate(steamId, steamId);
        if (account.IsBanned)
            return new OperationResponse(OpAuthenticate, -3, "Account banned", new());

        return new OperationResponse(OpAuthenticate, 0, null, new()
        {
            { PAddress, _masterAddress },
            { PSecret, AuthToken.Mint(steamId, _secret) },
            { PUserId, steamId },
        });
    }
```
(Add `using BlackIce.Server.Data;` and reference the project: `dotnet add BlackIce.Server.LoadBalancing reference BlackIce.Server.Data` from `$REPO/server`.)

- [ ] **Step 5: Add ban re-check to Master & Game Authenticate**

In `MasterServerHandler` and `GameServerHandler`, add an `AccountService _accounts` ctor field, and after validating the token (which yields the SteamID via `AuthToken.TryValidate(token, _secret, out var steamId)`), reject if `_accounts.Find(steamId)?.IsBanned == true`:
```csharp
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token && AuthToken.TryValidate(token, _secret, out var steamId))
        {
            if (_accounts.Find(steamId)?.IsBanned == true)
                return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
            return new OperationResponse(OpAuthenticate, 0, null, new());
        }
```
Add the `AccountService accounts` parameter to both constructors (after the existing params) and store it. Update the LAN-anonymous branch to still work (anonymous → no SteamID account check).

- [ ] **Step 6: Run identity tests + full suite**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter IdentityTests 2>&1 | tail -4
dotnet test "$REPO/BlackIce.sln" 2>&1 | grep -E "Passed!|Failed!" | head
```
Expected: identity tests PASS; fix any constructor call sites in other tests (they now need an `AccountService`; construct one over a `TestDb`).

- [ ] **Step 7: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(lb): resolve accounts on auth, carry SteamID in token, reject bans"
```

---

## Task 9: Host — config, schema, bootstrap, console loop

**Files:** Create `BlackIce.Server.Host/ServerConfig.cs`; Modify `Program.cs`.

- [ ] **Step 1: Config record + loader**

`BlackIce.Server.Host/ServerConfig.cs`:
```csharp
using System.Text.Json;
using BlackIce.Server.Data;

namespace BlackIce.Server.Host;

public sealed class ServerConfig
{
    public string AdvertisedHost { get; set; } = "127.0.0.1";
    public bool AllowAnonymousLan { get; set; } = true;
    public string TestRoomName { get; set; } = "[CUSTOM SERVER] Test Room";
    public DatabaseOptions Database { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var def = new ServerConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
            return def;
        }
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }
}
```

- [ ] **Step 2: Rewrite Program.cs to wire config, DB, bootstrap, console loop**

`BlackIce.Server.Host/Program.cs`:
```csharp
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.Host;
using BlackIce.Server.LoadBalancing;

var config = ServerConfig.Load("blackice.server.json");
if (args.Length > 0) config.AdvertisedHost = args[0];
if (args.Contains("--require-token")) config.AllowAnonymousLan = false;
const string secret = "change-me-platform-sp1";

using var db = config.Database.CreateContext();      // EnsureCreated runs here
var accounts = new AccountService(db);

Console.WriteLine($"BlackIce.Server — DB {config.Database.Provider}, advertising {config.AdvertisedHost}");
Console.WriteLine($"*** One-time bootstrap code (redeem in-game once available): {accounts.EnsureBootstrapCode()} ***");

var registry = new RoomRegistry();
registry.GetOrCreate(config.TestRoomName);

var listeners = new[]
{
    new UdpListener("NameServer", 5058, new NameServerHandler($"{config.AdvertisedHost}:5055", secret, accounts)),
    new UdpListener("MasterServer", 5055, new MasterServerHandler($"{config.AdvertisedHost}:5056", secret, registry, config.AllowAnonymousLan, config.TestRoomName, accounts)),
    new UdpListener("GameServer", 5056, new GameServerHandler(secret, registry, config.AllowAnonymousLan, accounts)),
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Console admin command loop on a background thread (uses its own context for thread-safety).
var processor = new ConsoleCommandProcessor(new AccountService(config.Database.CreateContext()));
var consoleThread = new Thread(() =>
{
    Console.WriteLine("console ready — type 'help'.");
    string? line;
    while (!cts.IsCancellationRequested && (line = Console.ReadLine()) != null)
    {
        var outp = processor.Execute(line);
        if (!string.IsNullOrEmpty(outp)) Console.WriteLine(outp);
    }
}) { IsBackground = true };
consoleThread.Start();

Console.WriteLine("NS 5058 / Master 5055 / Game 5056");
try { await Task.WhenAll(listeners.Select(l => l.RunAsync(cts.Token))); }
catch (OperationCanceledException) { }
Console.WriteLine("BlackIce.Server stopped.");
```

> The Master/Game handler constructors gain a trailing `AccountService accounts` parameter
> (Task 8 Step 5); the calls above match. The Master keeps its `testRoomName` parameter from
> Phase 1's GameList feature.

- [ ] **Step 3: Build the whole solution**

```bash
cd "$REPO" && dotnet build BlackIce.sln 2>&1 | tail -4
```
Expected: `Build succeeded.` Fix any handler-constructor call sites flagged.

- [ ] **Step 4: Smoke-run the server, verify DB + bootstrap + console**

```bash
cd "$REPO/server"
rm -f blackice.db BlackIce.Server.Host/bin/Release/net8.0/blackice.db
dotnet build BlackIce.Server.Host -c Release 2>&1 | grep -E "Build succeeded|error" | head -1
( echo "help"; echo "list"; sleep 2 ) | dotnet BlackIce.Server.Host/bin/Release/net8.0/BlackIce.Server.Host.dll 127.0.0.1 &
sleep 6; pkill -f BlackIce.Server.Host
ls BlackIce.Server.Host/bin/Release/net8.0/blackice.db && echo "DB created"
```
Expected: prints the bootstrap code + `console ready`, `help` lists commands, the SQLite file is created.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git status -s | grep -iE '\.db$' && echo "WARN db staged" || true
echo "blackice.db" >> .gitignore; echo "*.db" >> .gitignore
git add server/ .gitignore && git commit -m "feat(host): config, DB bootstrap, one-time code, console command loop"
```

---

## Task 10: Client mod sends the SteamID

**Files:** Modify `plugins/BlackIce.Redirect/RedirectPlugin.cs`.

- [ ] **Step 1: Set AuthValues.UserId to the SteamID before connect**

In `ConnectRedirectPatch.Prefix` (runs just before `ConnectUsingSettings`), add:
```csharp
        try
        {
            if (Steamworks.SteamManager.Initialized)
            {
                var steamId = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
                PhotonNetwork.AuthValues = new Photon.Realtime.AuthenticationValues(steamId);
                RedirectPlugin.Log.LogInfo($"Sending SteamID {steamId} as Photon UserId");
            }
        }
        catch (System.Exception ex) { RedirectPlugin.Log.LogWarning($"SteamID unavailable: {ex.Message}"); }
```
Add a reference to the game's Steamworks assembly in `BlackIce.Redirect.csproj`:
```xml
    <Reference Include="com.rlabrecque.steamworks.net"><HintPath>$(GameManaged)\com.rlabrecque.steamworks.net.dll</HintPath><Private>false</Private></Reference>
```
(Confirm the exact Steamworks DLL name first: `ls "$MANAGED" | grep -i steam`. Use the actual file name in the HintPath and the `Steamworks` namespace it provides.)

- [ ] **Step 2: Build (auto-deploys to BepInEx/plugins)**

```bash
cd "$REPO/plugins/BlackIce.Redirect" && dotnet build -c Release 2>&1 | grep -E "Build succeeded|error" | head
```
Expected: `Build succeeded.` and the DLL copied into the game's `BepInEx/plugins`.

- [ ] **Step 3: Commit**

```bash
cd "$REPO" && git add plugins/BlackIce.Redirect/ && git status -s | grep -iE '/bin/|/obj/|\.dll$' || echo clean
git commit -m "feat(redirect): send SteamID as Photon UserId for server-side accounts"
```

---

## Task 11: Live integration — account auto-created from a real connect

**Files:** none (acceptance test).

- [ ] **Step 1: Run the server (fresh DB) and connect the real client**

```bash
cd "$REPO/server"
powershell.exe -NoProfile -Command "(Get-NetUDPEndpoint -LocalPort 5058 -ErrorAction SilentlyContinue).OwningProcess | %{ Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }" 2>/dev/null
rm -f BlackIce.Server.Host/bin/Release/net8.0/blackice.db
nohup dotnet BlackIce.Server.Host/bin/Release/net8.0/BlackIce.Server.Host.dll 127.0.0.1 > /tmp/bi-server.log 2>&1 &
sleep 5
powershell.exe -NoProfile -Command "Start-Process -FilePath 'C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice.exe'"
sleep 60
powershell.exe -NoProfile -Command "Get-Process 'Black Ice' -ErrorAction SilentlyContinue | Stop-Process -Force"
```

- [ ] **Step 2: Verify the account row was created from the real SteamID**

```bash
cd "$REPO/server"
( echo "list"; sleep 1 ) | dotnet BlackIce.Server.Host/bin/Release/net8.0/BlackIce.Server.Host.dll 127.0.0.1 2>&1 | grep -E "7656|Player|Console" | head
grep -iE "Sending SteamID|new peer|op 230" /tmp/bi-server.log | head
```
Expected: the `list` output shows a real `7656…` SteamID account at level Player; the BepInEx log shows "Sending SteamID …". (If it shows a GUID instead of a 7656… id, the mod's SteamID send didn't apply — check the Steamworks DLL name from Task 10 Step 1.)

- [ ] **Step 3: Stop server and record the result**

```bash
pkill -f BlackIce.Server.Host
```
Append a short "SP1 verified" note (account auto-created from live SteamID) to `docs/protocol/01-connection-flow.md` or `server/ORACLE.md`.

- [ ] **Step 4: Commit**

```bash
cd "$REPO" && git add docs/ server/ORACLE.md 2>/dev/null; git commit -m "test(platform-sp1): account auto-created from live SteamID connect" || echo "nothing to commit"
```

---

## Self-Review

**Spec coverage:**
- §3 EF Core data project, SQLite default + MySQL via config → Tasks 1, 3, 9. ✓
- §4 Account/Profile/ServerState + PlayerLevel → Task 2. ✓
- §5 identity resolution (SteamID as UserId, auto-create, fallback, ban) → Tasks 4, 8, 10. ✓
- §6 bootstrap code + console loop (promote/demote/ban/unban/list/code/help) → Tasks 6, 7, 9. ✓
- §7 handler integration (auth resolves account, rejects bans; Host wiring) → Tasks 8, 9. ✓
- §8 error handling (DB fatal at startup via CreateContext; refuse-not-crash) → Task 9 (CreateContext throws → process exits); handler ban-refuse is non-fatal. ✓
- §9 testing (in-memory account-service tests; existing tests pass) → Tasks 2,4,5,6,7,8. ✓
- §2 DoD (persistent account at Player; bootstrap code printed; console promote/ban/list; banned refused; SQLite default) → Tasks 8,9,11. ✓

**Placeholder scan:** No "TBD/TODO". Two ground-truth confirm steps (Task 8 Step 3 AuthToken no-op check; Task 10 Steam DLL name) are explicit verifications, not vagueness. The `EnsureCreated`-vs-migrations choice is called out in the header.

**Type consistency:** `AccountService` methods (`ResolveOrCreate`, `Find`, `SetLevel`, `SetBanned`, `All`, `EnsureBootstrapCode`, `ClaimBootstrap`) are defined in Tasks 4–6 and used consistently in Tasks 7–9. `PlayerLevel` (0–3) used uniformly. Handler constructors gain a trailing `AccountService accounts` parameter (Task 8) and the Host calls match (Task 9). `DatabaseOptions.CreateContext()` defined in Task 3, used in Task 9. `RandomNumberGenerator.GetHexString` is .NET 8.

**Known confirm-at-build points:** the exact Steamworks DLL name (Task 10) and any test call sites that construct handlers without an `AccountService` (Task 8 Step 6 fixes them) — both have explicit steps.
