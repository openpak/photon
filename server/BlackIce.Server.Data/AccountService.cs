using BlackIce.Server.Common;
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

    public Account? Find(string steamId) =>
        _db.Accounts.Include(a => a.Profile).FirstOrDefault(a => a.SteamId == steamId);

    /// <summary>Sets an account's permission tier. <see cref="ErrorCode.NotFound"/> if no such account.</summary>
    public Result SetLevel(string steamId, PlayerLevel level)
    {
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return Result.Fail(ErrorCode.NotFound);
        acct.Level = level;
        _db.SaveChanges();
        return Result.Ok;
    }

    /// <summary>Bans or unbans an account. <see cref="ErrorCode.NotFound"/> if no such account.</summary>
    public Result SetBanned(string steamId, bool banned)
    {
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return Result.Fail(ErrorCode.NotFound);
        acct.IsBanned = banned;
        _db.SaveChanges();
        return Result.Ok;
    }

    public IReadOnlyList<Account> All() => _db.Accounts.OrderByDescending(a => a.Level).ToList();

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

    /// <summary>
    /// Redeems the bootstrap code once, promoting the account to Console. Distinguishes its failure
    /// modes (a plain bool would not): <see cref="ErrorCode.BadState"/> when no code exists or it was
    /// already claimed, <see cref="ErrorCode.PermissionDenied"/> on a wrong code, and
    /// <see cref="ErrorCode.NotFound"/> when the account doesn't exist.
    /// </summary>
    public Result ClaimBootstrap(string steamId, string code)
    {
        var state = _db.ServerState.Find(1);
        if (state is null || string.IsNullOrEmpty(state.BootstrapCode)) return Result.Fail(ErrorCode.BadState);
        if (state.BootstrapClaimed) return Result.Fail(ErrorCode.BadState);
        if (!string.Equals(state.BootstrapCode, code, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.PermissionDenied);
        var acct = _db.Accounts.FirstOrDefault(a => a.SteamId == steamId);
        if (acct is null) return Result.Fail(ErrorCode.NotFound);
        acct.Level = PlayerLevel.Console;
        state.BootstrapClaimed = true;
        _db.SaveChanges();
        return Result.Ok;
    }
}
