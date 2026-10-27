namespace Fleet.Core;

/// <summary>One line in an entity's human-readable timeline (shown in the fleet UI).</summary>
public record EntityEvent(DateTime At, string Kind, string Text);

public static class EntityEventListExtensions
{
    public static void AddCapped(this List<EntityEvent> list, EntityEvent item, int cap = 40)
    {
        list.Add(item);
        if (list.Count > cap)
        {
            list.RemoveRange(0, list.Count - cap);
        }
    }
}
