using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>
/// In-game "/command" chat surface: a "/"-prefixed chat RPC is executed through the CommandRegistry at the
/// caller's level, gated per-command by MinLevel, with /help and /commands listing what that level may run.
/// The caller's level is the VERIFIED level (computed by GameServerHandler); here we pass it directly.
/// </summary>
public class ChatCommandTests
{
    private sealed class TestCommands
    {
        [ConsoleCommand("ping", MinLevel = PlayerLevel.Player)]
        public string Ping(CommandLine line) => "pong";

        [ConsoleCommand("secretcmd", MinLevel = PlayerLevel.Admin)]
        public string Secret(CommandLine line) => "the secret";

        [ConsoleCommand("blob", MinLevel = PlayerLevel.Player)]
        public string Blob(CommandLine line) => new string('x', 500);   // a deliberately oversized reply
    }

    [Fact]
    public void Oversized_reply_is_chunked_into_multiple_small_messages()
    {
        // Regression: a single ~1.5 KB /help line disconnected the game client. Any long reply must be split
        // into several ServerMessages, each within the chat-line budget — so it never overflows the client.
        var replies = Handler().TryHandle("co-op", Chat("/blob"), PlayerLevel.Player);
        Assert.NotNull(replies);
        Assert.True(replies!.Count > 1, $"expected a 500-char reply to be chunked, got {replies.Count}");
        foreach (var ev in replies)
            Assert.True(((string)ev.Parameters[PhotonCodes.Param.Data]).Length <= 180);
    }

    private static ChatCommandHandler Handler() =>
        new(realms: null, motd: null, new CommandRegistry().Register(new TestCommands()));

    // Build the chat RPC OperationRequest the way a PUN ReceiveChatMessage arrives.
    private static OperationRequest Chat(string text) => new(PhotonCodes.Op.RaiseEvent, new()
    {
        { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
        { PhotonCodes.Param.Data, new Dictionary<byte, object>
            {
                { PhotonCodes.RpcKey.MethodName, "ReceiveChatMessage" },
                { PhotonCodes.RpcKey.Args, new object[] { text } },
            }
        },
    });

    // Replies are now chunked into one-or-more ServerMessages; join them so text assertions span all chunks.
    private static string? Reply(IReadOnlyList<EventData>? evs) =>
        evs is null ? null : string.Join(" ",
            evs.Select(e => e.Parameters.TryGetValue(PhotonCodes.Param.Data, out var t) ? t as string : null));

    [Fact]
    public void Player_can_run_a_player_command()
    {
        var reply = Handler().TryHandle("co-op", Chat("/ping"), PlayerLevel.Player);
        Assert.Equal("pong", Reply(reply));
    }

    [Fact]
    public void Player_is_refused_an_admin_command()
    {
        var reply = Handler().TryHandle("co-op", Chat("/secretcmd"), PlayerLevel.Player);
        Assert.Contains("requires Admin", Reply(reply));
        Assert.DoesNotContain("the secret", Reply(reply));
    }

    [Fact]
    public void Admin_can_run_an_admin_command()
    {
        var reply = Handler().TryHandle("co-op", Chat("/secretcmd"), PlayerLevel.Admin);
        Assert.Equal("the secret", Reply(reply));
    }

    [Fact]
    public void Help_lists_only_commands_the_level_may_run()
    {
        var asPlayer = Reply(Handler().TryHandle("co-op", Chat("/help"), PlayerLevel.Player));
        Assert.Contains("ping", asPlayer);
        Assert.DoesNotContain("secretcmd", asPlayer);

        var asAdmin = Reply(Handler().TryHandle("co-op", Chat("/help"), PlayerLevel.Admin));
        Assert.Contains("ping", asAdmin);
        Assert.Contains("secretcmd", asAdmin);
    }

    [Fact]
    public void Commands_is_an_alias_for_help()
    {
        var reply = Reply(Handler().TryHandle("co-op", Chat("/commands"), PlayerLevel.Player));
        Assert.Contains("ping", reply);
    }

    [Fact]
    public void Unknown_command_is_reported()
    {
        var reply = Reply(Handler().TryHandle("co-op", Chat("/nope"), PlayerLevel.Admin));
        Assert.Contains("Unknown command", reply);
    }

    [Fact]
    public void Normal_chat_is_not_intercepted()
    {
        Assert.Null(Handler().TryHandle("co-op", Chat("hello everyone"), PlayerLevel.Admin));
    }
}
