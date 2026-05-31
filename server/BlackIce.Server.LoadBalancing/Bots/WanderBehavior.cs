namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>A simple random-walk bounded to a radius around the spawn point. Deterministic with a seed.</summary>
public sealed class WanderBehavior : IBotBehavior
{
    private readonly float _ox, _oz, _radius;
    private readonly Random _rng;
    private float _x, _z;

    public WanderBehavior(float startX, float startZ, int? seed = null, float radius = 10f)
    {
        _ox = _x = startX; _oz = _z = startZ; _radius = radius;
        _rng = seed is int s ? new Random(s) : new Random();
    }

    public BotPositionUpdate Tick()
    {
        _x = Clamp(_x + (float)(_rng.NextDouble() * 2 - 1), _ox);
        _z = Clamp(_z + (float)(_rng.NextDouble() * 2 - 1), _oz);
        return new BotPositionUpdate(_x, 0f, _z);
    }

    private float Clamp(float v, float origin) => Math.Clamp(v, origin - _radius, origin + _radius);
}
