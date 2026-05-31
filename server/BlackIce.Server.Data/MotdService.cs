using BlackIce.Server.Common;

namespace BlackIce.Server.Data;

/// <summary>Resolves and edits the Message of the Day: a per-realm override over a global default.</summary>
public sealed class MotdService
{
    private readonly BlackIceDbContext _db;
    public MotdService(BlackIceDbContext db) => _db = db;

    /// <summary>Effective MOTD for a realm: the realm override if set, else the global, else null.</summary>
    public string? Resolve(Realm? realm)
    {
        if (!string.IsNullOrWhiteSpace(realm?.Motd)) return realm!.Motd;
        var global = State().Motd;
        return string.IsNullOrWhiteSpace(global) ? null : global;
    }

    public string? GetGlobal() => State().Motd;

    public void SetGlobal(string? text)
    {
        var s = State();
        s.Motd = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        _db.SaveChanges();
    }

    /// <summary>Sets a realm's MOTD override. <see cref="ErrorCode.NotFound"/> if no such realm exists.</summary>
    public Result SetRealm(string realmName, string? text)
    {
        var r = _db.Realms.FirstOrDefault(x => x.Name == realmName);
        if (r is null) return Result.Fail(ErrorCode.NotFound);
        r.Motd = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        _db.SaveChanges();
        return Result.Ok;
    }

    /// <summary>The single ServerState row, created on first use.</summary>
    private ServerState State()
    {
        var s = _db.ServerState.Find(1);
        if (s is null)
        {
            s = new ServerState { Id = 1 };
            _db.ServerState.Add(s);
            _db.SaveChanges();
        }
        return s;
    }
}
