using Shared;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

public sealed class AssassinAI : BaseAI
{
    public AssassinAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.30;
    protected override double MpPotionWeightFraction => 0.30;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.AttackSpeed, Stat.Accuracy, Stat.Agility };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinAC, Stat.MaxAC, Stat.Accuracy, Stat.Agility };

    protected override IEnumerable<Spell> GetAttackSpells()
    {
        if (Client.DoubleSlash)
            yield return Spell.DoubleSlash;
    }

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (Client.HasMagic(Spell.DoubleSlash))
        {
            if (!Client.DoubleSlash)
                await Client.ToggleSpellAsync(Spell.DoubleSlash, true);

            var spell = Client.DoubleSlash ? Spell.DoubleSlash : Spell.None;
            await AttackWithSpellAsync(current, monster, spell);
        }
        else
        {
            await AttackWithSpellAsync(current, monster, Spell.None);
        }
    }
}
