using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;

namespace InsanityRevive;

// Plain key="value" parser for cfg/insanity.cfg. We bypass cvars entirely
// — registering FakeConVars and then waiting for ExecuteCommand to apply
// them is async and order-dependent; reading a file is synchronous and
// deterministic. The cfg syntax is the same the user expects from the
// console, so they can keep editing the same file.
public sealed class Config
{
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string LogsRoot =
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "logs");
    private static readonly string CfgPath =
        Path.Combine(Server.GameDirectory, "csgo", "cfg", "insanity.cfg");
    private static readonly string DefaultRealSteamIds =
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp",
            "configs", "plugins", "InsanityRevive", "real_steamids.txt");

    public Config()
    {
        if (!File.Exists(CfgPath))
        {
            Log.Warn($"insanity.cfg not found at {CfgPath} — using defaults");
            return;
        }

        foreach (var raw in File.ReadAllLines(CfgPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//") || line.StartsWith("#")) continue;
            // Format: key "value"   or   key value
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            var key = line[..space].Trim();
            var val = line[(space + 1)..].Trim().Trim('"');
            if (key.Length > 0) _kv[key] = val;
        }
    }

    public string SteamIdMode      => Get("insanity_steamid_mode", "synthetic");
    public string RealSteamIdsFile => Get("insanity_real_steamids_file", DefaultRealSteamIds);
    public int    DefaultBotCount  => GetInt("insanity_default_bot_count", 5);
    public string LogLevel         => Get("insanity_log_level", "info");
    public bool   ApplyBotNavPatch => GetInt("insanity_apply_botnav_patch", 0) != 0;
    public string TelemetryPath    => Resolve(Get("insanity_telemetry_path",
        Path.Combine(LogsRoot, "insanity", "{date}_{session}.jsonl")));

    private string Get(string k, string fallback)
        => _kv.TryGetValue(k, out var v) && v.Length > 0 ? v : fallback;

    private int GetInt(string k, int fallback)
        => int.TryParse(Get(k, fallback.ToString()), out var v) ? v : fallback;

    private static string Resolve(string p)
        => Path.IsPathRooted(p) ? p : Path.Combine(Server.GameDirectory, p);
}
