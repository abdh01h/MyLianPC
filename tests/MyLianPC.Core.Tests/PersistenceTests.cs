using MyLianPC.Core.Cooling;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Settings;

namespace MyLianPC.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MyLianPCTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ProfileStore NewStore()
    {
        var s = new ProfileStore(Path.Combine(_dir, "profiles.json"));
        s.Load();
        return s;
    }

    [Fact]
    public void First_load_seeds_valid_example_profiles()
    {
        var store = NewStore();
        var names = store.Snapshot.Profiles.Select(p => p.Name).ToList();
        Assert.Equal(["Gaming", "Work", "Silent", "Night Mode", "Custom RGB", "Maximum Cooling"], names);
        Assert.All(store.Snapshot.Profiles, p => Assert.Empty(p.Validate()));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Profiles_and_active_selection_survive_reload()
    {
        var store = NewStore();
        var work = store.Snapshot.Profiles.Single(p => p.Name == "Work");
        store.SetActive(work.Id);
        var copy = store.Duplicate(work.Id, "Work (late)");

        var reloaded = NewStore();
        Assert.Equal(work.Id, reloaded.Active?.Id);
        var loadedCopy = reloaded.Get(copy.Id);
        Assert.Equal(RgbColor.White, loadedCopy.Lighting!.SyncAll!.Colors[0]);
        Assert.Equal(work.Cooling!.Curves["hydroshift:fans"].Points, loadedCopy.Cooling!.Curves["hydroshift:fans"].Points);
    }

    [Fact]
    public void Per_account_active_profile_is_remembered_and_survives_reload()
    {
        var store = NewStore();
        var gaming = store.Snapshot.Profiles.Single(p => p.Name == "Gaming");
        var work = store.Snapshot.Profiles.Single(p => p.Name == "Work");

        store.SetActiveFor("S-1-5-21-111", gaming.Id);
        store.SetActiveFor("S-1-5-21-222", work.Id);

        Assert.Equal(gaming.Id, store.ActiveFor("S-1-5-21-111")?.Id);
        Assert.Equal(work.Id, store.ActiveFor("S-1-5-21-222")?.Id);
        // An unknown account falls back to the machine-wide selection (the last one activated).
        Assert.Equal(work.Id, store.ActiveFor("S-1-5-21-333")?.Id);
        Assert.Equal(work.Id, store.Active?.Id);

        var reloaded = NewStore();
        Assert.Equal(gaming.Id, reloaded.ActiveFor("S-1-5-21-111")?.Id);
        Assert.Equal(work.Id, reloaded.ActiveFor("S-1-5-21-222")?.Id);
    }

    [Fact]
    public void Deleting_a_profile_forgets_per_account_choices_pointing_at_it()
    {
        var store = NewStore();
        var gaming = store.Snapshot.Profiles.Single(p => p.Name == "Gaming");
        store.SetActiveFor("S-1-5-21-111", gaming.Id);

        store.Delete(gaming.Id);

        Assert.DoesNotContain("S-1-5-21-111", store.Snapshot.ActiveByUser.Keys);
        Assert.Null(store.ActiveFor("S-1-5-21-111"));
    }

    [Fact]
    public void Rejects_invalid_curve_and_duplicate_names()
    {
        var store = NewStore();
        var bad = new HardwareProfile
        {
            Id = Guid.NewGuid(),
            Name = "Broken",
            Cooling = new CoolingSettings
            {
                Curves = new Dictionary<string, FanCurve>
                {
                    ["mb:SYS_FAN1"] = new() { Source = TemperatureSource.CpuPackage, Points = [new(30, 20), new(95, 40)] },
                },
            },
        };
        Assert.Throws<ProfileValidationException>(() => store.Upsert(bad));
        Assert.Throws<ProfileValidationException>(() => store.Upsert(bad with { Cooling = null, Name = "gaming" }));
    }

    [Fact]
    public void Deleting_active_profile_clears_selection()
    {
        var store = NewStore();
        var id = store.Snapshot.Profiles[0].Id;
        store.SetActive(id);
        store.Delete(id);
        Assert.Null(store.Snapshot.ActiveProfileId);
        Assert.Throws<KeyNotFoundException>(() => store.Get(id));
    }

    [Fact]
    public void Export_import_creates_new_profile_with_unique_name()
    {
        var store = NewStore();
        var gaming = store.Snapshot.Profiles.Single(p => p.Name == "Gaming");
        var imported = store.Import(store.Export(gaming.Id));
        Assert.NotEqual(gaming.Id, imported.Id);
        Assert.Equal("Gaming (2)", imported.Name);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(gaming.Lighting, Json.Options), System.Text.Json.JsonSerializer.Serialize(imported.Lighting, Json.Options));
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_defaults_restored()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "profiles.json");
        File.WriteAllText(path, "{ not json");
        var store = new ProfileStore(path);
        store.Load();
        Assert.NotEmpty(store.Snapshot.Profiles);
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void User_preferences_default_to_dark_and_round_trip_independently()
    {
        var path = Path.Combine(_dir, "prefs.json");
        Assert.Equal(AppearanceMode.Dark, UserPreferences.Load(path).Appearance);

        var store = NewStore();
        var before = File.ReadAllText(store.FilePath);
        new UserPreferences { Appearance = AppearanceMode.System }.Save(path);

        Assert.Equal(AppearanceMode.System, UserPreferences.Load(path).Appearance);
        Assert.Equal(before, File.ReadAllText(store.FilePath)); // theme change never touches hardware profiles
    }
}
