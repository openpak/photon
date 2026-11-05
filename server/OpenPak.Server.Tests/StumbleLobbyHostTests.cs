using OpenPak.Server.LoadBalancing.Quantum;
using Xunit;

namespace OpenPak.Server.Tests;

public class StumbleLobbyHostTests
{
    private readonly List<IDictionary<object, object>> _sets = new();
    private StumbleLobbyHost Host() => new((room, props) => _sets.Add(new Dictionary<object, object>(props))) { JoinWindow = 5, LoadTimeout = 30 };

    [Fact]
    public void Nothing_is_sent_before_the_join_window_ends()
    {
        var h = Host();
        h.Joined("r", 1, 0);
        h.Tick(4.9);
        Assert.Empty(_sets);
    }

    [Fact]
    public void The_initial_properties_carry_the_rounds_with_the_types_the_client_reads()
    {
        var rounds = Host().InitialProperties("r");
        Assert.Equal((byte)1, rounds["ROUNDS_NUMBER"]);                 // byte
        Assert.Equal("IPL_047_Flame", rounds["ROUND1_MAP_ID"]);         // string, LevelDef id
        Assert.Equal("ipl_047_flame", rounds["ROUND1_MAP"]);            // string, MapCollection name
        Assert.IsType<string>(rounds["ROUND1_MAP_NAME"]);
        Assert.Equal(3684277463294234336L, rounds["ROUND1_MAP_GUID"]);  // long
        Assert.Equal("IPL_047_Flame", rounds["MAP_ID"]);                // the scene loader's current round
        Assert.Equal("ipl_047_flame", rounds["MAP"]);
        Assert.True(rounds.ContainsKey("GAME_ID"));
    }

    [Fact]
    public void Extras_can_be_trimmed_and_rounds_clamped()
    {
        var h = Host();
        h.Extras = new HashSet<string>();
        h.RoundsNumber = 9;
        var p = h.InitialProperties("r");
        Assert.Equal((byte)2, p["ROUNDS_NUMBER"]);
        Assert.False(p.ContainsKey("GAME_ID") || p.ContainsKey("ROUND1_MAP_GUID"));
    }

    [Fact]
    public void After_the_window_LOAD_is_set_once()
    {
        var h = Host();
        h.Joined("r", 1, 0);
        h.Tick(5);
        Assert.Equal(true, Assert.Single(_sets)["LOAD"]);
        h.Tick(6);
        Assert.Single(_sets);
    }

    [Fact]
    public void START_follows_when_every_player_is_loaded()
    {
        var h = Host();
        h.Joined("r", 1, 0); h.Joined("r", 2, 1);
        h.Tick(5);
        _sets.Clear();
        h.PlayerProperties("r", 1, new Dictionary<object, object> { ["LOAD_READY"] = true });
        h.Tick(6);
        Assert.Empty(_sets);            // actor 2 still loading
        h.PlayerProperties("r", 2, new Dictionary<object, object> { ["LOAD_READY"] = true });
        h.Tick(7);
        Assert.Single(_sets);
        Assert.Equal(true, _sets[0]["START"]);
    }

    [Fact]
    public void START_follows_the_load_timeout_for_a_player_that_never_reports()
    {
        var h = Host();
        h.Joined("r", 1, 0);
        h.Tick(5);
        _sets.Clear();
        h.Tick(34.9);
        Assert.Empty(_sets);
        h.Tick(35);
        Assert.Equal(true, Assert.Single(_sets)["START"]);
    }

    [Fact]
    public void A_player_leaving_before_the_end_does_not_hold_the_start()
    {
        var h = Host();
        h.Joined("r", 1, 0); h.Joined("r", 2, 0);
        h.Tick(5);
        _sets.Clear();
        h.PlayerProperties("r", 1, new Dictionary<object, object> { ["LOAD_READY"] = true });
        h.Left("r", 2);
        h.Tick(6);
        Assert.Equal(true, Assert.Single(_sets)["START"]);
    }
}
