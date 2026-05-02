using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InsanityRevive;

// In-memory cache + JSON-backed persistence for Persona records.
// Single owner: FakeClientManager. Single thread: main game thread.
// No concurrent access invariant — every call is on the same thread.
//
// Persistence model:
//   - Load on plugin Load() — if file exists, deserialize; else empty.
//   - Save on every mutation (atomic rename via temp+rename).
//   - File is small (10s of KB even at thousands of personas), and
//     mutations are infrequent (mapchange, spawn-new-persona).
//
// Persona.Id is monotonic: max(existing) + 1 on insert. Never reused
// even after deletion (prevents debug confusion / accidental aliasing
// of deleted personas to new ones).
public sealed class PersonaRegistry
{
    public const string DefaultPath = "/home/frad70/cs2-server/insanity/personas.json";

    private readonly string _path;
    private readonly Dictionary<int, Persona> _byId = new();
    private int _nextId = 1;

    public PersonaRegistry(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public string Path => _path;

    /// <summary>Total persona count (active + dormant).</summary>
    public int Count => _byId.Count;

    /// <summary>Currently bound (ActiveOnSlot != null) personas.</summary>
    public IEnumerable<Persona> Active => _byId.Values.Where(p => p.IsActive);

    /// <summary>All personas, in Id order.</summary>
    public IEnumerable<Persona> All => _byId.Values.OrderBy(p => p.Id);

    public void Load()
    {
        try {
            if (!File.Exists(_path)) {
                Log.Info($"PersonaRegistry: no file at {_path}, starting empty");
                return;
            }
            var json = File.ReadAllText(_path);
            var arr = JsonSerializer.Deserialize<List<Persona>>(json) ?? new();
            _byId.Clear();
            foreach (var p in arr) {
                if (p.Id <= 0 || string.IsNullOrEmpty(p.Name)) continue;
                _byId[p.Id] = p;
                if (p.Id >= _nextId) _nextId = p.Id + 1;
                // Volatile field — never trust ActiveOnSlot from disk.
                // (Could be left over from a non-clean shutdown.)
                p.ActiveOnSlot = null;
            }
            Log.Info($"PersonaRegistry: loaded {_byId.Count} personas " +
                     $"(nextId={_nextId}) from {_path}");
        } catch (Exception ex) {
            Log.Error($"PersonaRegistry load failed: {ex.Message} — starting empty");
            _byId.Clear();
            _nextId = 1;
        }
    }

    public void Save()
    {
        try {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            var arr = _byId.Values.OrderBy(p => p.Id).ToList();
            var json = JsonSerializer.Serialize(arr,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        } catch (Exception ex) {
            Log.Error($"PersonaRegistry save failed: {ex.Message}");
        }
    }

    public Persona? GetById(int id) =>
        _byId.TryGetValue(id, out var p) ? p : null;

    public Persona? GetActiveOnSlot(int slot) =>
        _byId.Values.FirstOrDefault(p => p.ActiveOnSlot == slot);

    /// <summary>
    /// LRU + Id tie-break ordering for dormant persona selection. Dormant
    /// personas with empty LastSeenAt sort first (treated as oldest); ties
    /// resolved by ascending Id for stable behavior on a fresh registry.
    /// </summary>
    private static IEnumerable<Persona> OrderForReuse(IEnumerable<Persona> source) =>
        source
            .OrderBy(p => string.IsNullOrEmpty(p.LastSeenAt)
                          ? "0001-01-01T00:00:00.0000000Z"
                          : p.LastSeenAt, StringComparer.Ordinal)
            .ThenBy(p => p.Id);

    /// <summary>
    /// Pick (or create) a persona to spawn. Selection policy:
    /// 1. Prefer a dormant (ActiveOnSlot==null) persona whose Name is
    ///    not in <paramref name="reservedNames"/>. LRU + Id tie-break
    ///    so on a fresh registry the order is deterministic by Id, and
    ///    on a mature registry it spreads load across all personas.
    /// 2. If all dormant ones are reserved, fall through to fallback
    ///    roster — pick the first roster name not yet in registry and
    ///    not in reservedNames; create + persist a new Persona.
    /// 3. Last resort: synthesize "player&lt;Id&gt;" if even the
    ///    fallback roster is exhausted.
    ///
    /// Does NOT bind to a slot — caller is responsible for BindToSlot
    /// after the engine assigns a slot via OCC/CPiS.
    /// </summary>
    public Persona AcquireForSpawn(
        IReadOnlyList<string> fallbackRoster,
        ISet<string> reservedNames)
    {
        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // (1) Reuse a dormant persona.
        var dormant = OrderForReuse(_byId.Values.Where(p => !p.IsActive))
            .FirstOrDefault(p => !reservedNames.Contains(p.Name));
        if (dormant != null) {
            dormant.LastSeenAt = nowIso;
            Save();
            return dormant;
        }

        // (2) Mint from fallback roster.
        var existingNames = new HashSet<string>(
            _byId.Values.Select(p => p.Name), StringComparer.Ordinal);
        var newName = fallbackRoster
            .FirstOrDefault(n => !reservedNames.Contains(n)
                              && !existingNames.Contains(n));

        // (3) Synthesize as last resort.
        newName ??= $"player{_nextId}";

        var persona = new Persona(_nextId++, newName, nowIso);
        _byId[persona.Id] = persona;
        Save();
        return persona;
    }

    /// <summary>
    /// Bind a persona to a player slot — called after successful adoption
    /// in OnClientPutInServer. Updates ActiveOnSlot + LastSeenAt and saves.
    /// </summary>
    public void BindToSlot(int personaId, int slot)
    {
        if (!_byId.TryGetValue(personaId, out var p)) return;
        p.ActiveOnSlot = slot;
        p.LastSeenAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Save();
    }

    /// <summary>
    /// Release a persona's slot binding without deleting the persona.
    /// Called from OnClientDisconnect (real-disconnect path) and from
    /// Despawn flows. Mapchange path doesn't call this — it preserves
    /// ActiveOnSlot until OnMapStart's snapshot.
    /// </summary>
    public void ReleaseSlot(int personaId)
    {
        if (!_byId.TryGetValue(personaId, out var p)) return;
        p.ActiveOnSlot = null;
        Save();
    }

    /// <summary>
    /// Wipe ActiveOnSlot for all personas — called from OnMapStart
    /// AFTER snapshot. Single Save() at the end instead of per-clear.
    /// </summary>
    public void ClearAllActiveSlots()
    {
        bool any = false;
        foreach (var p in _byId.Values) {
            if (p.ActiveOnSlot != null) { p.ActiveOnSlot = null; any = true; }
        }
        if (any) Save();
    }

    /// <summary>
    /// Hard delete — removes persona by id. Doesn't reuse the id.
    /// Use sparingly; normal flow is just ReleaseSlot.
    /// </summary>
    public bool Remove(int personaId)
    {
        if (!_byId.Remove(personaId)) return false;
        Save();
        return true;
    }
}
