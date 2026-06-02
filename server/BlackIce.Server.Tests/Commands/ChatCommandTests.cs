using System.Collections.Generic;
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

    private static string? Reply(EventData? ev) =>
        ev?.Parameters.TryGetValue(PhotonCodes.Param.Data, out var t) == true ? t as string : null;

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
