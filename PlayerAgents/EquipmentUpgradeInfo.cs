using Shared;

public sealed class EquipmentUpgradeInfo
{
    public ItemType Type { get; init; }
    public ItemInfo Item { get; init; }
    public NpcEntry Npc { get; init; }
    public int Count { get; set; }

    public EquipmentUpgradeInfo(ItemType type, ItemInfo item, NpcEntry npc, int count)
    {
        Type = type;
        Item = item;
        Npc = npc;
        Count = count;
    }
}
