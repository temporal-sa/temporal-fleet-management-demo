namespace Fleet.Core;

/// <summary>Maps the truck entity's operational status to the availability code the TMS understands.</summary>
public static class StatusMapping
{
    public static string ToTms(TruckStatus status) => status switch
    {
        TruckStatus.Available => "AVAILABLE",
        TruckStatus.Reserved => "ASSIGNED",
        TruckStatus.OnLoad => "ASSIGNED",
        TruckStatus.ShopBound => "IN_REPAIR",
        TruckStatus.InShop => "IN_REPAIR",
        TruckStatus.Breakdown => "DOWN",
        TruckStatus.OutOfService => "OUT_OF_SERVICE",
        _ => "UNKNOWN",
    };
}
