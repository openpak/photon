namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>Produces varied, free-form bot identities from fixed pools + a seeded RNG (deterministic for tests).</summary>
public sealed class BotIdentityGenerator
{
    private static readonly string[] Adjectives = { "Rogue", "Silent", "Iron", "Neon", "Ghost", "Razor", "Vex", "Null" };
    private static readonly string[] Nouns = { "Runner", "Spike", "Cipher", "Wraith", "Byte", "Hex", "Daemon", "Probe" };
    private readonly Random _rng;
    private int _counter;

    public BotIdentityGenerator(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();

    public BotIdentity Next()
    {
        var name = $"{Adjectives[_rng.Next(Adjectives.Length)]}{Nouns[_rng.Next(Nouns.Length)]}{++_counter}";
        var colors = new float[4][];
        for (int i = 0; i < 4; i++)
            colors[i] = new[] { (float)_rng.NextDouble(), (float)_rng.NextDouble(), (float)_rng.NextDouble(), 1f };
        return new BotIdentity(name, _rng.Next(0, 32), colors, Level: 0, Team: 1);
    }
}
