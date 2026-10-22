using System.Text.Json;
using MyLianPC.Core.Settings;

namespace MyLianPC.Core.Profiles;

public sealed record ProfileDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid? ActiveProfileId { get; init; }

    /// <summary>
    /// Which profile each Windows account last activated (keyed by SID). The profile list itself is shared by
    /// every account; this only remembers the choice so signing in can restore it.
    /// </summary>
    public IReadOnlyDictionary<string, Guid> ActiveByUser { get; init; } = new Dictionary<string, Guid>();

    public IReadOnlyList<HardwareProfile> Profiles { get; init; } = [];
}

/// <summary>
/// Machine-wide profile storage. Owned by the background service (the only writer); desktop
/// clients go through the service. Writes are atomic so a crash never leaves a torn file.
/// </summary>
public sealed class ProfileStore(string filePath)
{
    private readonly object _gate = new();
    private ProfileDocument _doc = new();

    public string FilePath { get; } = filePath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC", "profiles.json");

    public ProfileDocument Snapshot { get { lock (_gate) return _doc; } }

    public HardwareProfile? Active
    {
        get { lock (_gate) return _doc.Profiles.FirstOrDefault(p => p.Id == _doc.ActiveProfileId); }
    }

    /// <summary>The profile this Windows account (by SID) last activated, falling back to the machine-wide active one.</summary>
    public HardwareProfile? ActiveFor(string? sid)
    {
        lock (_gate)
        {
            if (sid is not null && _doc.ActiveByUser.TryGetValue(sid, out var id)
                && _doc.Profiles.FirstOrDefault(p => p.Id == id) is { } own) return own;
            return _doc.Profiles.FirstOrDefault(p => p.Id == _doc.ActiveProfileId);
        }
    }

    /// <summary>Loads from disk. A missing file seeds the defaults; a corrupt file is preserved as .corrupt and defaults are used.</summary>
    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                _doc = new ProfileDocument { Profiles = DefaultProfiles.All() };
                Save();
                return;
            }
            try
            {
                _doc = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), Json.Options)
                       ?? throw new JsonException("empty document");
            }
            catch (JsonException)
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                _doc = new ProfileDocument { Profiles = DefaultProfiles.All() };
                Save();
            }
        }
    }

    public HardwareProfile Upsert(HardwareProfile profile)
    {
        var problems = profile.Validate();
        if (problems.Count > 0) throw new ProfileValidationException(problems);
        lock (_gate)
        {
            if (_doc.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ProfileValidationException([$"A profile named '{profile.Name}' already exists."]);
            var stamped = profile with { ModifiedUtc = DateTimeOffset.UtcNow };
            _doc = _doc with { Profiles = [.. _doc.Profiles.Where(p => p.Id != profile.Id), stamped] };
            Save();
            return stamped;
        }
    }

    public HardwareProfile Duplicate(Guid id, string newName) =>
        Upsert(Get(id) with { Id = Guid.NewGuid(), Name = newName });

    public HardwareProfile Rename(Guid id, string newName) => Upsert(Get(id) with { Name = newName });

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            _doc = _doc with
            {
                Profiles = [.. _doc.Profiles.Where(p => p.Id != id)],
                ActiveProfileId = _doc.ActiveProfileId == id ? null : _doc.ActiveProfileId,
                ActiveByUser = _doc.ActiveByUser.Where(kv => kv.Value != id).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            Save();
        }
    }

    public void SetActive(Guid id)
    {
        lock (_gate)
        {
            Get(id);
            _doc = _doc with { ActiveProfileId = id };
            Save();
        }
    }

    /// <summary>Activates a profile and remembers it as this Windows account's choice.</summary>
    public void SetActiveFor(string? sid, Guid id)
    {
        lock (_gate)
        {
            Get(id);
            var byUser = new Dictionary<string, Guid>(_doc.ActiveByUser);
            if (!string.IsNullOrEmpty(sid)) byUser[sid] = id;
            _doc = _doc with { ActiveProfileId = id, ActiveByUser = byUser };
            Save();
        }
    }

    public HardwareProfile Get(Guid id)
    {
        lock (_gate)
            return _doc.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"Profile {id} not found.");
    }

    public string Export(Guid id) => JsonSerializer.Serialize(Get(id), Json.Options);

    /// <summary>Imports a profile exported by <see cref="Export"/>. It always gets a fresh id; a clashing name gets a suffix.</summary>
    public HardwareProfile Import(string json)
    {
        var p = JsonSerializer.Deserialize<HardwareProfile>(json, Json.Options) ?? throw new JsonException("empty profile");
        var name = p.Name;
        lock (_gate)
            for (int n = 2; _doc.Profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{p.Name} ({n})";
        return Upsert(p with { Id = Guid.NewGuid(), Name = name });
    }

    private void Save() => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_doc, Json.Options));
}

public sealed class ProfileValidationException(IReadOnlyList<string> problems)
    : Exception("Profile is invalid: " + string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
