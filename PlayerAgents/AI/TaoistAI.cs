using Shared;

public sealed class TaoistAI : BaseAI
{
    public TaoistAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.20;
    protected override double MpPotionWeightFraction => 0.40;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinSC, Stat.MaxSC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };
}
