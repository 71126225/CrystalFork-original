using Shared;
using System.Drawing;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class TaoistAI : BaseAI
{
    public TaoistAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.20;
    protected override double MpPotionWeightFraction => 0.40;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinSC, Stat.MaxSC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        await base.AttackMonsterAsync(monster, current);
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        return await base.MoveToTargetAsync(map, current, target, radius);
    }
}
