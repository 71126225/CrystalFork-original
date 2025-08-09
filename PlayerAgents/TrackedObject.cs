using System;
using System.Drawing;
using Shared;

public sealed class TrackedObject
{
    public uint Id { get; }
    public ObjectType Type { get; }
    public string Name { get; internal set; }
    public Point Location { get; set; }
    public MirDirection Direction { get; set; }

    public byte AI { get; }
    public bool Dead { get; set; }
    public bool Hidden { get; set; }
    public bool Tamed { get; set; }

    public PoisonType Poison { get; set; }
    public Spell Spell { get; }

    public byte? HealthPercent { get; set; }

    // Records which player this monster is currently engaged with and when that
    // engagement started. Null if not engaged.
    public uint? EngagedWith { get; set; }
    public DateTime LastEngagedTime { get; set; }

    public TrackedObject(uint id, ObjectType type, string name, Point location, MirDirection direction, byte ai = 0, bool dead = false, bool hidden = false, Spell spell = Spell.None)
    {
        Id = id;
        Type = type;
        Name = name;
        Location = location;
        Direction = direction;
        AI = ai;
        Dead = dead;
        Hidden = hidden;
        EngagedWith = null;
        LastEngagedTime = DateTime.MinValue;
        Poison = PoisonType.None;
        Spell = spell;
    }
}
