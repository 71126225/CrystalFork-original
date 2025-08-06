using Shared;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

public sealed class WarriorAI : BaseAI
{
    public WarriorAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.40;
    protected override double MpPotionWeightFraction => 0.20;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.AttackSpeed, Stat.Accuracy, Stat.Agility };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinAC, Stat.MaxAC, Stat.Accuracy, Stat.Agility };

    protected override IEnumerable<Spell> GetAttackSpells()
    {
        if (Client.Slaying)
            yield return Spell.Slaying;
        if (Client.Thrusting)
            yield return Spell.Thrusting;
    }

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (Client.HasMagic(Spell.Thrusting) && !Client.Thrusting)
            await Client.ToggleSpellAsync(Spell.Thrusting, true);

        Spell spell = Spell.None;
        if (Client.Thrusting)
        {
            var distance = Functions.MaxDistance(current, monster.Location);
            if (distance == 2)
            {
                spell = Spell.Thrusting;
            }
            else if (distance == 1)
            {
                var dir = Functions.DirectionFromPoint(current, monster.Location);
                var behind = Functions.PointMove(monster.Location, dir, 1);
                bool thrustObject = Client.TrackedObjects.Values.Any(o => o.Location == behind && o.Id != monster.Id);
                if (thrustObject)
                    spell = Spell.Thrusting;
            }
        }

        if (spell == Spell.None && Client.Slaying)
            spell = Spell.Slaying;

        await AttackWithSpellAsync(current, monster, spell);
    }
}
