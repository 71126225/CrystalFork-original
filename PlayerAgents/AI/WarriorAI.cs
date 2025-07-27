using Shared;

public sealed class WarriorAI : BaseAI
{
    public WarriorAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.40;
    protected override double MpPotionWeightFraction => 0.20;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.AttackSpeed, Stat.Accuracy, Stat.Agility };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinAC, Stat.MaxAC, Stat.Accuracy, Stat.Agility };
}
