using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class MotdSchemaTests
{
    [Fact]
    public void ServerState_and_Realm_persist_Motd()
    {
        using var db = new TestDb();
        db.Context.ServerState.Add(new ServerState { Id = 1, Motd = "global hi" });
        db.Context.Realms.Add(new Realm { Name = "co-op", Motd = "realm hi" });
        db.Context.SaveChanges();

        using var read = db.NewContext();
        Assert.Equal("global hi", read.ServerState.Find(1)!.Motd);
        Assert.Equal("realm hi", read.Realms.Find("co-op")!.Motd);
    }
}
