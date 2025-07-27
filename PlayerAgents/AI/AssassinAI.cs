using Shared;

public sealed class AssassinAI : BaseAI
{
    public AssassinAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.30;
    protected override double MpPotionWeightFraction => 0.30;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.AttackSpeed, Stat.Accuracy, Stat.Agility };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinAC, Stat.MaxAC, Stat.Accuracy, Stat.Agility };
}
