using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class RpcShortcutsTests
{
    [Fact]
    public void Resolves_known_indices_to_method_names()
    {
        Assert.Equal("KilledPlayerRemote", RpcShortcuts.Name(32));
        Assert.Equal("ReceiveChatMessage", RpcShortcuts.Name(39));
        Assert.Equal("TeleportImmediately", RpcShortcuts.Name(66));
        Assert.Equal("BecomeTangible", RpcShortcuts.Name(9));
    }

    [Fact]
    public void Has_the_full_captured_table_and_rejects_out_of_range()
    {
        Assert.Equal(88, RpcShortcuts.Methods.Count);
        Assert.Null(RpcShortcuts.Name(-1));
        Assert.Null(RpcShortcuts.Name(88));
    }
}
