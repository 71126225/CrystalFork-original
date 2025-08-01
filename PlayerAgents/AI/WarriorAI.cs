using Shared;
using System.Drawing;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class WarriorAI : BaseAI
{
    public WarriorAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.40;
    protected override double MpPotionWeightFraction => 0.20;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.AttackSpeed, Stat.Accuracy, Stat.Agility };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinAC, Stat.MaxAC, Stat.Accuracy, Stat.Agility };

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        var dir = Functions.DirectionFromPoint(current, monster.Location);
        if (Client.Slaying)
            await Client.AttackAsync(dir, Spell.Slaying);
        else
            await Client.AttackAsync(dir, Spell.None);
        RecordAttackTime();
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        return await base.MoveToTargetAsync(map, current, target, radius);
    }
}
