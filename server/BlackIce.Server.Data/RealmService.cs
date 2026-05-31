using BlackIce.Server.Common;

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
        if (existing is null)
        {
            _db.Realms.Add(realm);
        }
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

    /// <summary>Removes a realm. <see cref="ErrorCode.NotFound"/> if no realm by that name exists.</summary>
    public Result Delete(string name)
    {
        var r = _db.Realms.FirstOrDefault(x => x.Name == name);
        if (r is null) return Result.Fail(ErrorCode.NotFound);
        _db.Realms.Remove(r);
        _db.SaveChanges();
        return Result.Ok;
    }
}
