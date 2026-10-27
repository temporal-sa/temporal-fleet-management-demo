namespace Fleet.Core;

public record ShopInfo(
    string ShopId,
    string Name,
    string HubKey,
    bool IsPartner,
    IssueCategory[] Capabilities,
    int Bays,
    double LaborMultiplier,
    int AvgAcceptMinutes);

public record TruckSeed(
    string Unit,
    string Vin,
    string Make,
    string Model,
    int Year,
    string HomeHub,
    string StartHub,
    int Odometer,
    int PmDueOdometer,
    string? PairedDriverId);

public record DriverSeed(
    string DriverId,
    string Name,
    string HomeHub,
    string[] Endorsements,
    DriverEmployment Employment,
    string Phone,
    string? PairedUnit);

/// <summary>
/// Illustrative seed data for the demo. Shop locations, names, people and costs are invented;
/// swap them for real reference data when pointing the demo at a sandbox.
/// </summary>
public static class FleetSeed
{
    private static readonly IssueCategory[] AllCategories = Enum.GetValues<IssueCategory>();

    private static readonly IssueCategory[] LightService =
    {
        IssueCategory.Tires, IssueCategory.Brakes, IssueCategory.Electrical, IssueCategory.PreventiveService,
    };

    public static readonly IReadOnlyList<ShopInfo> Shops = new List<ShopInfo>
    {
        // Partner workshops: preferred shops with negotiated rates, roughly 70% below a general workshop invoice.
        new("P-OMA", "Omaha Partner Workshop", "OMA", true, AllCategories, 12, 1.0, 5),
        new("P-DFW", "Dallas Partner Workshop", "DFW", true, AllCategories, 10, 1.0, 5),
        new("P-PHX", "Phoenix Partner Workshop", "PHX", true, AllCategories, 8, 1.0, 5),
        new("P-FON", "Fontana Partner Workshop", "FON", true, AllCategories, 8, 1.0, 5),
        new("P-ATL", "Atlanta Partner Workshop", "ATL", true, AllCategories, 8, 1.0, 5),
        new("P-SGH", "Springfield Partner Workshop", "SGH", true, AllCategories, 6, 1.0, 5),
        new("P-CHI", "Chicago Partner Workshop", "CHI", true, LightService, 6, 1.0, 5),
        new("P-SLC", "Salt Lake Partner Workshop", "SLC", true, AllCategories, 4, 1.0, 5),

        // General workshops (fictional names), billed at standard rates.
        new("G-MKC", "Heartland Diesel Service", "MKC", false, AllCategories, 4, 1.00, 25),
        new("G-OKC", "Red Dirt Truck & Tire", "OKC", false, LightService, 3, 0.95, 20),
        new("G-AMA", "Panhandle Fleet Repair", "AMA", false, AllCategories, 3, 1.05, 30),
        new("G-ABQ", "High Desert Diesel", "ABQ", false, AllCategories, 3, 1.10, 30),
        new("G-ELP", "Border Fleet Works", "ELP", false, AllCategories, 4, 1.00, 25),
        new("G-DEN", "Front Range Truck Center", "DEN", false, AllCategories, 5, 1.15, 20),
        new("G-STL", "Gateway Heavy Duty", "STL", false, AllCategories, 4, 1.00, 25),
        new("G-MEM", "Bluff City Diesel", "MEM", false, AllCategories, 4, 0.98, 25),
        new("G-BNA", "Cumberland Truck Repair", "BNA", false, LightService, 3, 1.00, 20),
        new("G-IND", "Crossroads Fleet Service", "IND", false, AllCategories, 4, 1.02, 25),
        new("G-LIT", "Natural State Diesel", "LIT", false, AllCategories, 3, 1.00, 30),
        new("G-LRD", "Rio Bravo Diesel", "LRD", false, AllCategories, 4, 0.95, 25),
        new("G-HOU", "Bayou City Truck Service", "HOU", false, AllCategories, 5, 1.05, 20),
        new("G-CHI", "Lakeshore Heavy Truck", "CHI", false, AllCategories, 5, 1.20, 20),
        new("G-DSM", "Prairie Truck & Trailer", "DSM", false, AllCategories, 3, 1.00, 25),
        new("G-LBF", "Platte Valley Repair", "LBF", false, LightService, 2, 1.00, 35),
        new("G-CYS", "Wyoming Fleet Repair", "CYS", false, AllCategories, 2, 1.10, 35),
        new("G-SAC", "Valley Heavy Duty", "SAC", false, AllCategories, 4, 1.15, 25),
        new("G-BHM", "Magic City Diesel", "BHM", false, AllCategories, 3, 1.00, 25),
        new("G-CLT", "Piedmont Fleet Repair", "CLT", false, AllCategories, 4, 1.05, 25),
        new("G-PIT", "Three Rivers Truck", "PIT", false, AllCategories, 3, 1.10, 30),
        new("G-JAX", "First Coast Diesel", "JAX", false, AllCategories, 3, 1.00, 25),
        new("G-LAS", "Desert Fleet Service", "LAS", false, AllCategories, 3, 1.15, 25),
        new("G-TUS", "Old Pueblo Truck Repair", "TUS", false, AllCategories, 3, 1.00, 30),
        new("G-BOI", "Snake River Diesel", "BOI", false, AllCategories, 2, 1.05, 35),
        new("G-MSP", "North Star Truck Repair", "MSP", false, AllCategories, 4, 1.10, 25),
        new("G-DET", "Motor City Fleet Service", "DET", false, AllCategories, 4, 1.10, 25),
        new("G-SAT", "Alamo Truck & Tire", "SAT", false, AllCategories, 4, 1.00, 25),
    };

    public static readonly IReadOnlyDictionary<string, ShopInfo> ShopsById = Shops.ToDictionary(s => s.ShopId);

    public static readonly IReadOnlyList<TruckSeed> Trucks = new List<TruckSeed>
    {
        new("41201", "3AKJHHDR7RSUA1201", "Freightliner", "Cascadia", 2024, "OMA", "OMA", 182_400, 190_000, "D2001"),
        new("41202", "1XKYD49X5RJ411202", "Kenworth", "T680", 2024, "DFW", "DFW", 214_900, 220_500, "D2002"),
        new("41203", "1XPBD49X2RD411203", "Peterbilt", "579", 2024, "PHX", "PHX", 301_250, 309_000, "D2003"),
        new("41204", "4V4NC9EH3RN411204", "Volvo", "VNL 760", 2024, "FON", "FON", 256_780, 260_000, "D2004"),
        new("41205", "3AKJHHDR9RSUA1205", "Freightliner", "Cascadia", 2023, "ATL", "ATL", 398_120, 398_900, "D2005"),
        new("41206", "1XKYD49X7RJ411206", "Kenworth", "T680", 2023, "SGH", "SGH", 344_600, 352_000, "D2006"),
        new("41207", "3HSDZAPR4RN411207", "International", "LT625", 2023, "OMA", "CHI", 287_330, 296_000, "D2007"),
        new("41208", "4V4NC9EH5RN411208", "Volvo", "VNL 760", 2023, "SLC", "SLC", 412_050, 420_000, "D2008"),
        new("41209", "3AKJHHDR1RSUA1209", "Freightliner", "Cascadia", 2024, "OMA", "MKC", 156_700, 165_000, "D2009"),
        new("41210", "1XPBD49X6RD411210", "Peterbilt", "579", 2022, "SLC", "DEN", 489_900, 492_000, "D2010"),
        new("41211", "1XKYD49X9RJ411211", "Kenworth", "T680", 2024, "DFW", "MEM", 198_340, 205_000, "D2011"),
        new("41212", "3AKJHHDR3RSUA1212", "Freightliner", "Cascadia", 2023, "SGH", "IND", 322_480, 330_000, "D2012"),
        new("41213", "4V4NC9EH7RN411213", "Volvo", "VNL 760", 2024, "DFW", "LRD", 174_220, 180_000, "D2013"),
        new("41214", "3HSDZAPR6RN411214", "International", "LT625", 2022, "DFW", "OKC", 455_610, 462_000, "D2014"),
        new("41215", "1XPBD49X8RD411215", "Peterbilt", "579", 2023, "OMA", "STL", 365_140, 372_000, "D2015"),
        new("41216", "3AKJHHDR5RSUA1216", "Freightliner", "Cascadia", 2024, "ATL", "BHM", 141_870, 150_000, "D2016"),
        new("41217", "1XKYD49X1RJ411217", "Kenworth", "T680", 2023, "ATL", "CLT", 276_330, 284_000, "D2017"),
        new("41218", "4V4NC9EH9RN411218", "Volvo", "VNL 760", 2022, "PHX", "ABQ", 501_200, 508_000, "D2018"),
        new("41219", "3HSDZAPR8RN411219", "International", "LT625", 2024, "OMA", "DSM", 133_450, 140_000, "D2019"),
        new("41220", "3AKJHHDR7RSUA1220", "Freightliner", "Cascadia", 2023, "DFW", "HOU", 309_870, 318_000, "D2020"),
        new("41221", "1XPBD49X0RD411221", "Peterbilt", "579", 2024, "DFW", "LIT", 188_640, 196_000, "D2021"),
        new("41222", "1XKYD49X3RJ411222", "Kenworth", "T680", 2023, "SGH", "CMH", 297_510, 305_000, "D2022"),
        // Two slip-seat trucks parked at terminals without a driver. New hires (D2023, D2024) drive them.
        new("41223", "4V4NC9EH0RN411223", "Volvo", "VNL 760", 2024, "OMA", "OMA", 98_420, 105_000, null),
        new("41224", "3AKJHHDR9RSUA1224", "Freightliner", "Cascadia", 2024, "DFW", "DFW", 87_330, 95_000, null),
    };

    public static readonly IReadOnlyList<DriverSeed> Drivers = new List<DriverSeed>
    {
        new("D2001", "Marcus Bell", "OMA", Array.Empty<string>(), DriverEmployment.Active, "(402) 555-0101", "41201"),
        new("D2002", "Dana Whitfield", "DFW", Array.Empty<string>(), DriverEmployment.Active, "(214) 555-0102", "41202"),
        new("D2003", "Luis Ortega", "PHX", new[] { "Hazmat" }, DriverEmployment.Active, "(602) 555-0103", "41203"),
        new("D2004", "Tamika Greene", "FON", Array.Empty<string>(), DriverEmployment.Active, "(909) 555-0104", "41204"),
        new("D2005", "Brent Sorensen", "ATL", Array.Empty<string>(), DriverEmployment.Active, "(404) 555-0105", "41205"),
        new("D2006", "Priya Nair", "SGH", Array.Empty<string>(), DriverEmployment.Active, "(937) 555-0106", "41206"),
        new("D2007", "Cole Haverly", "OMA", new[] { "Hazmat", "Tanker" }, DriverEmployment.Active, "(402) 555-0107", "41207"),
        new("D2008", "Rosa Delgado", "SLC", Array.Empty<string>(), DriverEmployment.Active, "(801) 555-0108", "41208"),
        new("D2009", "Jamal Pruitt", "OMA", Array.Empty<string>(), DriverEmployment.Active, "(402) 555-0109", "41209"),
        new("D2010", "Erin Kowalski", "SLC", Array.Empty<string>(), DriverEmployment.Active, "(801) 555-0110", "41210"),
        new("D2011", "Hector Ramirez", "DFW", new[] { "Hazmat" }, DriverEmployment.Active, "(214) 555-0111", "41211"),
        new("D2012", "Megan Tully", "SGH", Array.Empty<string>(), DriverEmployment.Active, "(937) 555-0112", "41212"),
        new("D2013", "Darnell Hayes", "DFW", Array.Empty<string>(), DriverEmployment.Active, "(214) 555-0113", "41213"),
        new("D2014", "Kristin Lowe", "DFW", Array.Empty<string>(), DriverEmployment.Active, "(214) 555-0114", "41214"),
        new("D2015", "Andre Fontaine", "OMA", new[] { "Hazmat" }, DriverEmployment.Active, "(402) 555-0115", "41215"),
        new("D2016", "Wade Ellison", "ATL", Array.Empty<string>(), DriverEmployment.Active, "(404) 555-0116", "41216"),
        new("D2017", "Nadia Petrov", "ATL", Array.Empty<string>(), DriverEmployment.Active, "(404) 555-0117", "41217"),
        new("D2018", "Calvin Brooks", "PHX", Array.Empty<string>(), DriverEmployment.Active, "(602) 555-0118", "41218"),
        new("D2019", "Sofia Marquez", "OMA", new[] { "Hazmat" }, DriverEmployment.Active, "(402) 555-0119", "41219"),
        new("D2020", "Trent Gallagher", "DFW", Array.Empty<string>(), DriverEmployment.Active, "(214) 555-0120", "41220"),
        new("D2021", "Imani Carter", "DFW", Array.Empty<string>(), DriverEmployment.Active, "(214) 555-0121", "41221"),
        new("D2022", "Ray Lindqvist", "SGH", Array.Empty<string>(), DriverEmployment.Active, "(937) 555-0122", "41222"),
        new("D2023", "Jordan Pike", "OMA", Array.Empty<string>(), DriverEmployment.Onboarding, "(402) 555-0123", "41223"),
        new("D2024", "Alicia Moreno", "DFW", Array.Empty<string>(), DriverEmployment.Onboarding, "(214) 555-0124", "41224"),
    };

    public static readonly string[] NewHireNames =
    {
        "Evan Holt", "Grace Okafor", "Miguel Santos", "Hannah Reyes", "Owen Fairchild", "Leah Brandt",
    };

    public static readonly (string Shipper, string Commodity, bool Hazmat)[] Shippers =
    {
        ("Prairie Harvest Foods", "Dry grocery", false),
        ("Bluestem Paper Co.", "Paper products", false),
        ("Summit Home Goods", "Home goods", false),
        ("Cardinal Beverage", "Beverages", false),
        ("Ironwood Building Supply", "Building materials", false),
        ("Great Plains Ag Supply", "Fertilizer", true),
        ("Northline Appliances", "Appliances", false),
        ("Coastal Produce Partners", "Produce", false),
        ("Keystone Auto Parts", "Auto parts", false),
        ("Redbud Retail DC", "Retail mixed", false),
        ("Lone Peak Chemical", "Industrial cleaners", true),
    };

    public static readonly string[] OnboardingSteps =
    {
        "Background check", "Drug screen", "MVR review", "Orientation", "ELD account",
    };

    /// <summary>Simulated minutes each onboarding step takes. Kept short so the demo moves.</summary>
    public static readonly double[] OnboardingStepSimMinutes = { 90, 60, 45, 120, 30 };
}
