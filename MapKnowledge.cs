namespace InsanityRevive;

/// <summary>
/// Lightweight per-map data tables — common engagement zones, rotation
/// paths, "long-range" zones (where awps love to camp), default stack
/// locations. NOT a path-finder — just a string-key lookup that other
/// modules can read to decorate behavior.
///
/// v0.11 — scaffold. Won't be wired until parallel branches settle.
/// </summary>
public static class MapKnowledge
{
    public class MapInfo
    {
        public string Name = "";
        /// <summary>Common engagement zones — bots walk-shift when entering these.</summary>
        public string[] LongRangeZones = Array.Empty<string>();
        /// <summary>Common stack locations for site defense.</summary>
        public string[] StackSpots = Array.Empty<string>();
        /// <summary>Common entry corridors (bots may pre-aim default angles in these).</summary>
        public string[] EntryCorridors = Array.Empty<string>();
        /// <summary>Site labels (A/B). Most maps have two sites.</summary>
        public string[] SiteLabels = { "A", "B" };
    }

    private static readonly Dictionary<string, MapInfo> _maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_dust2"] = new MapInfo
        {
            Name = "de_dust2",
            LongRangeZones = new[] { "long a", "mid doors", "tunnels", "b doors", "ct cross" },
            StackSpots = new[] { "goose", "default a", "site b", "back of a", "platform b" },
            EntryCorridors = new[] { "long doors", "mid", "lower tunnels", "catwalk" },
        },
        ["de_mirage"] = new MapInfo
        {
            Name = "de_mirage",
            LongRangeZones = new[] { "mid", "connector", "short", "apartments", "palace", "jungle" },
            StackSpots = new[] { "site a default", "ticket booth", "site b stack", "kitchen", "underpass" },
            EntryCorridors = new[] { "ramp", "palace", "apps", "underpass", "connector" },
        },
        ["de_inferno"] = new MapInfo
        {
            Name = "de_inferno",
            LongRangeZones = new[] { "banana", "mid", "second mid", "library", "balcony" },
            StackSpots = new[] { "pit", "graveyard", "site b new box", "site a coffin", "library" },
            EntryCorridors = new[] { "banana", "apartments", "second mid", "underpass", "library" },
        },
        ["de_nuke"] = new MapInfo
        {
            Name = "de_nuke",
            LongRangeZones = new[] { "outside", "lobby", "ramp", "secret", "yard" },
            StackSpots = new[] { "heaven", "site a default", "site b lockers", "vents", "rafters" },
            EntryCorridors = new[] { "ramp", "outside", "lobby", "secret", "vents" },
        },
        ["de_overpass"] = new MapInfo
        {
            Name = "de_overpass",
            LongRangeZones = new[] { "long", "monster", "connector", "bathrooms", "fountain" },
            StackSpots = new[] { "default a", "site b heaven", "playground", "monster", "toilets" },
            EntryCorridors = new[] { "monster", "connector", "long", "short", "playground" },
        },
        ["de_anubis"] = new MapInfo
        {
            Name = "de_anubis",
            LongRangeZones = new[] { "main a", "main b", "mid", "connector", "water" },
            StackSpots = new[] { "site a default", "site b heaven", "water", "ramp", "palace" },
            EntryCorridors = new[] { "main", "connector", "water", "ramp", "palace" },
        },
        ["de_ancient"] = new MapInfo
        {
            Name = "de_ancient",
            LongRangeZones = new[] { "main", "mid", "ramp", "donut", "cubby" },
            StackSpots = new[] { "site a heaven", "default a", "donut", "ramp", "house" },
            EntryCorridors = new[] { "main", "mid", "donut", "ramp" },
        },
        ["de_vertigo"] = new MapInfo
        {
            Name = "de_vertigo",
            LongRangeZones = new[] { "ramp", "mid", "back site", "stairs", "elevator" },
            StackSpots = new[] { "site a heaven", "ct ramp", "site b boost", "elevator", "back plat" },
            EntryCorridors = new[] { "ramp", "stairs", "mid" },
        },
        ["de_train"] = new MapInfo
        {
            Name = "de_train",
            LongRangeZones = new[] { "ivy", "lower", "z", "pop dog", "alley" },
            StackSpots = new[] { "ivy", "z", "lower b", "ladder", "yard" },
            EntryCorridors = new[] { "ivy", "alley", "olof", "lower b", "ladder" },
        },
        ["de_cache"] = new MapInfo
        {
            Name = "de_cache",
            LongRangeZones = new[] { "main", "mid", "checkers", "garage", "highway" },
            StackSpots = new[] { "default a", "site b stack", "checkers", "highway", "vent" },
            EntryCorridors = new[] { "main", "mid", "checkers", "highway", "vent" },
        },
    };

    public static MapInfo GetCurrent(string mapName)
    {
        if (_maps.TryGetValue(mapName, out var m)) return m;
        return new MapInfo
        {
            Name = mapName,
            LongRangeZones = new[] { "long", "mid", "main" },
            StackSpots = new[] { "site a", "site b" },
            EntryCorridors = new[] { "main", "mid" },
            SiteLabels = new[] { "A", "B" },
        };
    }

    /// <summary>Heuristic: does this map have long sightlines that strongly favor awps?</summary>
    public static bool FavorsAwp(string mapName) => mapName switch
    {
        "de_dust2"   => true,
        "de_train"   => true,
        "de_overpass"=> true,
        "de_nuke"    => true,
        _            => false,
    };

    /// <summary>Cheap rotation timer (seconds) — used by callouts to gauge "how
    /// late is too late to call rotate". Approximate values from MM.</summary>
    public static float RotationTimeSec(string mapName) => mapName switch
    {
        "de_dust2"    => 7.0f,
        "de_mirage"   => 6.5f,
        "de_inferno"  => 8.0f,
        "de_nuke"     => 9.0f,
        "de_overpass" => 7.5f,
        "de_anubis"   => 7.5f,
        "de_ancient"  => 6.5f,
        "de_vertigo"  => 8.0f,
        "de_train"    => 9.5f,
        "de_cache"    => 7.0f,
        _             => 7.5f,
    };
}
