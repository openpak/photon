using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class ChatCommandTests
{
    private static OperationRequest ChatRpc(string text) => new(253, new()
    {
        { 244, (byte)200 },   // PUN RPC event code
        { 245, new Dictionary<object, object>
            {
                { (byte)3, "ReceiveChatMessage" },
                { (byte)4, new object[] { text } },
            } },
    });

    /// <summary>Chat RPC in PUN's shortcut form: method as a byte index at key 5 (no key-3 name).</summary>
    private static OperationRequest ShortcutChatRpc(string text, byte shortcutIndex = 7) => new(253, new()
    {
        { 244, (byte)200 },
        { 245, new Dictionary<object, object>
            {
                { (byte)0, 1001 },              // viewID
                { (byte)5, shortcutIndex },     // method shortcut index (no string name)
                { (byte)4, new object[] { text } },
            } },
    });

    private static ChatCommandHandler Handler(out TestDb db)
    {
        db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var motd = new MotdService(db.Context);
        motd.SetGlobal("Daily news: the Ice grows.");
        return new ChatCommandHandler(new RealmService(db.Context), motd);
    }

    [Fact]
    public void Slash_motd_returns_server_message_with_resolved_text()
    {
        var h = Handler(out var db);
        using (db)
        {
            var ev = h.TryHandle("co-op", ChatRpc("/motd"));
            Assert.NotNull(ev);
            Assert.Equal(199, ev![0].Code);
            Assert.Equal("Daily news: the Ice grows.", ev[0].Parameters[245]);
        }
    }

    [Fact]
    public void Unknown_slash_command_returns_hint()
    {
        var h = Handler(out var db);
        using (db)
        {
            var ev = h.TryHandle("co-op", ChatRpc("/frobnicate"));
            Assert.NotNull(ev);
            Assert.Contains("Unknown command", (string)ev![0].Parameters[245]);
        }
    }

    [Fact]
    public void Plain_chat_is_not_a_command()
    {
        var h = Handler(out var db);
        using (db)
            Assert.Null(h.TryHandle("co-op", ChatRpc("hello everyone")));
    }

    [Fact]
    public void Non_chat_raise_event_is_ignored()
    {
        var h = Handler(out var db);
        using (db)
        {
            var notChat = new OperationRequest(253, new() { { 244, (byte)201 }, { 245, new Dictionary<object, object>() } });
            Assert.Null(h.TryHandle("co-op", notChat));
        }
    }

    // PUN sends RPCs in the project's RpcList as a byte shortcut (key 5) instead of the string
    // name (key 3). The live client almost certainly chats via the shortcut form, so /commands
    // must be recognized there too — see B3 finding.
    [Fact]
    public void Shortcut_form_slash_motd_is_recognized()
    {
        var h = Handler(out var db);
        using (db)
        {
            var ev = h.TryHandle("co-op", ShortcutChatRpc("/motd"));
            Assert.NotNull(ev);
            Assert.Equal("Daily news: the Ice grows.", ev![0].Parameters[245]);
        }
    }

    [Fact]
    public void Shortcut_form_plain_chat_is_not_intercepted()
    {
        var h = Handler(out var db);
        using (db)
            Assert.Null(h.TryHandle("co-op", ShortcutChatRpc("hello everyone")));
    }

    // A hostile peer can put a non-byte (or out-of-range) value under the event-code key 244.
    // It must be treated as "not our RPC event" — ignored, never an exception (a real PUN client
    // always sends the code as a GpBinary byte).
    [Fact]
    public void Malformed_event_code_is_ignored_not_thrown()
    {
        var h = Handler(out var db);
        using (db)
        {
            var bad = new OperationRequest(253, new()
            {
                { 244, "not-a-byte" },
                { 245, new Dictionary<object, object> { { (byte)3, "ReceiveChatMessage" }, { (byte)4, new object[] { "/motd" } } } },
            });
            Assert.Null(h.TryHandle("co-op", bad));
        }
    }
}
