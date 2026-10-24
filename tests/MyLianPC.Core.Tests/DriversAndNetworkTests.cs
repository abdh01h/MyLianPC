using MyLianPC.Core.Drivers;
using MyLianPC.Core.Network;

namespace MyLianPC.Core.Tests;

public class TrafficMeterTests
{
    private static readonly DateTime Start = new(2026, 10, 20, 12, 0, 0);

    [Fact]
    public void First_sample_only_establishes_a_baseline()
    {
        var meter = new TrafficMeter(Start, 0, 0);
        var reading = meter.Add("wifi", 5_000, 2_000, Start);
        Assert.Equal(TrafficReading.Zero, reading);
        Assert.Equal(0, meter.DownloadedBytes);
        Assert.Equal(0, meter.UploadedBytes);
    }

    [Fact]
    public void Deltas_become_rates_and_daily_totals()
    {
        var meter = new TrafficMeter(Start, 0, 0);
        meter.Add("wifi", 1_000, 500, Start);
        var reading = meter.Add("wifi", 3_000, 1_500, Start.AddSeconds(2));

        Assert.Equal(1_000, reading.DownloadBytesPerSecond);
        Assert.Equal(500, reading.UploadBytesPerSecond);
        Assert.Equal(2_000, meter.DownloadedBytes);
        Assert.Equal(1_000, meter.UploadedBytes);
    }

    [Fact]
    public void A_counter_reset_never_subtracts_from_the_totals()
    {
        var meter = new TrafficMeter(Start, 0, 0);
        meter.Add("wifi", 10_000, 5_000, Start);
        var reading = meter.Add("wifi", 400, 200, Start.AddSeconds(1));

        Assert.Equal(TrafficReading.Zero, reading);
        Assert.Equal(0, meter.DownloadedBytes);
        Assert.Equal(0, meter.UploadedBytes);
    }

    [Fact]
    public void A_new_day_starts_from_zero()
    {
        var meter = new TrafficMeter(new DateTime(2026, 10, 20), 0, 0);
        meter.Add("wifi", 1_000, 1_000, new DateTime(2026, 10, 20, 23, 59, 0));
        meter.Add("wifi", 2_000, 1_500, new DateTime(2026, 10, 20, 23, 59, 30));
        Assert.Equal(1_000, meter.DownloadedBytes);

        // The first sample of the new day is a new baseline: the previous day's counters must not leak into it.
        var first = meter.Add("wifi", 500_000, 400_000, new DateTime(2026, 10, 21, 0, 0, 5));
        Assert.Equal(TrafficReading.Zero, first);
        Assert.Equal(0, meter.DownloadedBytes);
        Assert.Equal(new DateTime(2026, 10, 21), meter.DayStart);

        meter.Add("wifi", 502_000, 401_000, new DateTime(2026, 10, 21, 0, 0, 7));
        Assert.Equal(2_000, meter.DownloadedBytes);
        Assert.Equal(1_000, meter.UploadedBytes);
    }

    [Fact]
    public void Interfaces_are_tracked_separately_but_add_up()
    {
        var meter = new TrafficMeter(Start, 0, 0);
        meter.Add("wifi", 100, 100, Start);
        meter.Add("ethernet", 1_000, 1_000, Start);

        meter.Add("wifi", 300, 200, Start.AddSeconds(1));       // +200 down, +100 up on wifi
        meter.Add("ethernet", 1_200, 1_100, Start.AddSeconds(1)); // +200 down, +100 up on ethernet

        Assert.Equal(400, meter.DownloadedBytes);
        Assert.Equal(200, meter.UploadedBytes);
    }

    [Fact]
    public void Persisted_totals_are_restored_and_continue()
    {
        var meter = new TrafficMeter(Start, 8_000, 3_000);
        Assert.Equal(8_000, meter.DownloadedBytes);
        Assert.Equal(3_000, meter.UploadedBytes);

        meter.Add("wifi", 1_000, 1_000, Start);
        meter.Add("wifi", 1_500, 1_200, Start.AddSeconds(1));
        Assert.Equal(8_500, meter.DownloadedBytes);
        Assert.Equal(3_200, meter.UploadedBytes);
    }

    [Fact]
    public void Absurd_time_gaps_do_not_produce_rates()
    {
        var meter = new TrafficMeter(Start, 0, 0);
        meter.Add("wifi", 1_000, 1_000, Start);
        var reading = meter.Add("wifi", 2_000, 1_000, Start.AddHours(2));
        Assert.Equal(TrafficReading.Zero, reading);
        Assert.Equal(1_000, meter.DownloadedBytes); // the bytes still count towards the day
    }

    [Theory]
    [InlineData(0, "0 MB/s")]
    [InlineData(512, "0 MB/s")]
    [InlineData(1024, "0 MB/s")]
    [InlineData(5 * 1024 * 1024, "5 MB/s")]
    [InlineData(1_572_864, "1.5 MB/s")]
    [InlineData(double.NaN, "0 MB/s")]
    public void Rates_are_always_in_megabytes(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, NetworkText.Rate(bytesPerSecond));

    [Theory]
    [InlineData(2_500_000_000L, "2.5 Gbps")]
    [InlineData(866_700_000L, "866.7 Mbps")]
    [InlineData(null, "—")]
    public void Link_speed_is_human_readable(long? bitsPerSecond, string expected) =>
        Assert.Equal(expected, NetworkText.LinkSpeed(bitsPerSecond));

    [Fact]
    public void Mac_addresses_are_grouped() =>
        Assert.Equal("A4:BB:6D:11:22:33", NetworkText.Mac("A4BB6D112233"));

    [Theory]
    [InlineData(0, "0 GB")]
    [InlineData(820L * 1024 * 1024, "0.8 GB")]
    [InlineData(3_670_016_589, "3.42 GB")]
    [InlineData(-5, "0 GB")]
    public void Daily_totals_are_always_in_gigabytes(long bytes, string expected) =>
        Assert.Equal(expected, NetworkText.TodayBytes(bytes));
}

public class VersionsTests
{
    [Theory]
    [InlineData("11.29.50.0202", "11.29.50.202", 0)]
    [InlineData("18.4032.2510.901", "18.4032.2510.0901", 0)]
    [InlineData("1.2", "1.10", -1)]
    [InlineData("6.0.9927.1", "6.0.9926.9", 1)]
    [InlineData("1.0", "1.0.0.0", 0)]
    [InlineData("2", "1.99.99", 1)]
    public void Numeric_versions_compare_by_value(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(Versions.CompareNumeric(left, right) ?? int.MaxValue));

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", "")]
    [InlineData("", "")]
    [InlineData("amd", "1.0")]
    public void Incomparable_versions_return_null(string? left, string? right) =>
        Assert.Null(Versions.CompareNumeric(left, right));

    [Theory]
    [InlineData("F12d", "F12e", -1)]
    [InlineData("F12e", "F12d", 1)]
    [InlineData("F12", "F12a", -1)]
    [InlineData("F12e", "F12e", 0)]
    [InlineData("F9", "F13", -1)]
    [InlineData("f12e", "F12E", 0)]
    public void Bios_versions_compare_by_letter_number_then_suffix(string installed, string latest, int expected) =>
        Assert.Equal(expected, Math.Sign(Versions.CompareBios(installed, latest) ?? int.MaxValue));

    [Theory]
    [InlineData("12e", "F13")]
    [InlineData("F", "F13")]
    [InlineData("F12e", "")]
    public void Invalid_bios_versions_return_null(string installed, string latest) =>
        Assert.Null(Versions.CompareBios(installed, latest));
}

public class GigabyteSupportParserTests
{
    private const string Html = """
        <html><body>
        <h2>BIOS</h2>
        <table>
        <tr class="item-group">
        <td class="item-version">F12d</td><td class="item-date">2025/08/01</td><td class="item-size">12 MB</td>
        <td><a href="https://download.gigabyte.com/bios-f12d.zip">Download</a></td>
        <td><ul><li>Fix fan curve behaviour</li></ul></td>
        </tr>
        <tr class="item-group">
        <td class="item-version">F12e</td><td class="item-date">2025/09/01</td><td class="item-size">12 MB</td>
        <td><a href="https://download.gigabyte.com/bios-f12e.zip">Download</a></td>
        <td><ul><li>Improve memory compatibility</li><li>Update AGESA</li></ul></td>
        </tr>
        </table>
        <h2>Chipset</h2>
        <table>
        <tr class="item-group">
        <td><span class="support-item-desc">AMD Chipset Driver</span></td>
        <td class="item-version">7.05.15.1222</td><td class="item-os">Windows 11 64bit</td>
        <td class="item-date">2025/07/10</td><td class="item-size">99 MB</td>
        <td><a href="https://download.gigabyte.com/chipset-old.zip">Download</a></td>
        </tr>
        <tr class="item-group">
        <td><span class="support-item-desc">AMD Chipset Driver</span></td>
        <td class="item-version">7.05.15.1234</td><td class="item-os">Windows 11 64bit<br>Windows 10 64bit</td>
        <td class="item-date">2025/09/10</td><td class="item-size">100 MB</td>
        <td><a href="https://download.gigabyte.com/chipset.zip">Download</a></td>
        </tr>
        <tr class="item-group">
        <td><span class="support-item-desc">Windows 10 Only Tool</span></td>
        <td class="item-version">1.0</td><td class="item-os">Windows 10 64bit</td>
        <td class="item-date">2020/01/01</td><td class="item-size">1 MB</td>
        <td><a href="https://download.gigabyte.com/old.zip">Download</a></td>
        </tr>
        </table>
        <h2>Audio</h2>
        <table>
        <tr class="item-group">
        <td><span class="support-item-desc">Realtek HD Audio Driver (Note) Windows setup only</span></td>
        <td class="item-version">6.0.9927.1</td><td class="item-os">Windows 11 64bit</td>
        <td class="item-date">2025/05/05</td><td class="item-size">50 MB</td>
        <td><a href="https://download.gigabyte.com/audio.zip">Download</a></td>
        </tr>
        </table>
        </body></html>
        """;

    private static SupportCatalog Parse() =>
        GigabyteSupportParser.Parse(Html, "https://example/support", "Test board", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Bios_entries_are_newest_first_with_notes_and_links()
    {
        var catalog = Parse();
        Assert.Equal(2, catalog.Bios.Count);
        Assert.Equal("F12e", catalog.Bios[0].Version);
        Assert.Equal("F12d", catalog.Bios[1].Version);
        Assert.Equal("2025/09/01", catalog.Bios[0].Date);
        Assert.Equal("https://download.gigabyte.com/bios-f12e.zip", catalog.Bios[0].DownloadUrl);
        Assert.Equal(["Improve memory compatibility", "Update AGESA"], catalog.Bios[0].Notes);
    }

    [Fact]
    public void Windows_10_only_packages_are_skipped()
    {
        var catalog = Parse();
        Assert.DoesNotContain(catalog.Drivers, d => d.Name.Contains("Windows 10 Only"));
        Assert.DoesNotContain(catalog.Drivers, d => d.Os.Count > 0 && !d.Os.Any(o => o.Contains("Windows 11")));
    }

    [Fact]
    public void Duplicate_packages_keep_the_newest_version()
    {
        var chipset = Assert.Single(Parse().Drivers, d => d.Name == "AMD Chipset Driver");
        Assert.Equal("7.05.15.1234", chipset.Version);
        Assert.Equal("https://download.gigabyte.com/chipset.zip", chipset.DownloadUrl);
    }

    [Fact]
    public void Categories_follow_the_support_page_order_and_notes_are_stripped_from_names()
    {
        var drivers = Parse().Drivers;
        Assert.Equal(["AMD Chipset Driver", "Realtek HD Audio Driver"], drivers.Select(d => d.Name));
        Assert.Equal(["Chipset", "Audio"], drivers.Select(d => d.Category));
    }

    [Fact]
    public void A_page_without_a_download_list_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => GigabyteSupportParser.Parse("<html><h2>Overview</h2><p>Nothing</p></html>", "u", "b", DateTimeOffset.UtcNow));
}

public class DriverMatcherTests
{
    private static readonly DriverEntry Audio = new("Audio", "Realtek HD Audio Driver", "6.0.9927.1", "2025/05/05", "50 MB",
        ["Windows 11 64bit"], "https://example/audio");
    private static readonly DriverEntry Lan = new("LAN", "Realtek LAN Driver", "11.29.50.202", "2025/01/01", "10 MB",
        ["Windows 11 64bit"], "https://example/lan");
    private static readonly DriverEntry Wifi = new("WLAN+BT", "Realtek WIFI Driver", "6102.24.143.0", "2025/01/01", "10 MB",
        ["Windows 11 64bit"], "https://example/wifi");
    private static readonly DriverEntry Apu = new("Chipset", "AMD APU Driver", "32.0.21025.10016", "2025/01/01", "700 MB",
        ["Windows 11 64bit"], "https://example/apu");
    private static readonly DriverEntry Raid = new("SATA RAID/AHCI", "AMD RAIDXpert2 Utility", "9.3.3.001", "2025/01/01", "60 MB",
        ["Windows 11 64bit"], "https://example/raid");

    [Fact]
    public void Equal_versions_are_up_to_date()
    {
        var status = DriverMatcher.Evaluate(Audio, [new("Realtek High Definition Audio", "6.0.9927.1", InstalledItem.Driver)]);
        Assert.Equal(DriverState.UpToDate, status.State);
        Assert.Equal("Up to date", status.Label);
    }

    [Fact]
    public void An_older_installed_version_offers_an_update()
    {
        var status = DriverMatcher.Evaluate(Audio, [new("Realtek High Definition Audio", "6.0.9800.1", InstalledItem.Driver)]);
        Assert.Equal(DriverState.UpdateAvailable, status.State);
        Assert.Equal("Update available", status.Label);
    }

    [Fact]
    public void A_newer_installed_driver_is_not_downgraded()
    {
        var status = DriverMatcher.Evaluate(Audio, [new("Realtek High Definition Audio", "6.0.9999.1", InstalledItem.Driver)]);
        Assert.Equal(DriverState.NewerInstalled, status.State);
    }

    [Fact]
    public void Version_formats_that_differ_only_in_leading_zeros_match()
    {
        // A PnP device reports 1125.29.50.202 where the setup package reports 11.29.50.0202 for the same driver.
        var device = new InstalledItem("Realtek PCIe 2.5GbE Family Controller", "1125.29.50.202", InstalledItem.Driver);
        var program = new InstalledItem("Realtek Ethernet Controller All-In-One Windows Driver", "11.29.50.0202", InstalledItem.Program);
        var status = DriverMatcher.Evaluate(Lan, [device, program]);
        Assert.Equal(DriverState.UpToDate, status.State);
        Assert.Equal(InstalledItem.Program, status.Installed?.Source);
    }

    [Fact]
    public void A_missing_device_is_reported_as_not_detected()
    {
        var status = DriverMatcher.Evaluate(Raid, [new("Realtek High Definition Audio", "6.0.9927.1", InstalledItem.Driver)]);
        Assert.Equal(DriverState.NotDetected, status.State);
        Assert.Equal("Not detected", status.Label);
        Assert.Null(status.Installed);
    }

    [Fact]
    public void The_amd_graphics_driver_is_left_to_the_user_to_compare()
    {
        var status = DriverMatcher.Evaluate(Apu, [new("AMD Radeon(TM) Graphics", "32.0.21025.10016", InstalledItem.Driver)]);
        Assert.Equal(DriverState.Manual, status.State);
        Assert.NotNull(status.Installed);
    }

    [Fact]
    public void Setup_only_packages_are_labelled_as_such()
    {
        var preinstall = new DriverEntry("SATA RAID/AHCI", "AMD RAID Preinstall Driver", "1.0", null, null, ["Windows 11 64bit"], "u");
        var status = DriverMatcher.Evaluate(preinstall, []);
        Assert.Equal(DriverState.SetupOnly, status.State);
    }

    [Fact]
    public void The_wifi_rule_prefers_the_pnp_driver_over_an_unrelated_program()
    {
        var pnp = new InstalledItem("Realtek 8922AE WiFi 7 802.11be PCIe Network Adapter", "6102.24.143.0", InstalledItem.Driver);
        var bluetooth = new InstalledItem("Realtek Bluetooth Adapter", "18.4032.2510.0901", InstalledItem.Driver);
        var status = DriverMatcher.Evaluate(Wifi, [bluetooth, pnp]);
        Assert.Equal(DriverState.UpToDate, status.State);
        Assert.Contains("8922", status.Installed?.Name);
    }
}

public class DashboardPreferencesTests
{
    [Fact]
    public void New_preferences_leave_the_cooler_card_hidden()
    {
        var keys = new MyLianPC.Core.Settings.UserPreferences().DashboardMetrics;
        Assert.DoesNotContain(keys, k => MyLianPC.Core.Sensors.Metrics.Find(k)?.Group == "Cooler");
        Assert.Contains(MyLianPC.Core.Sensors.Metrics.GpuVramTotal, keys);
    }

    [Fact]
    public void Version_five_drops_the_cooler_but_keeps_the_accounts_choices_and_adds_vram()
    {
        var preferences = new MyLianPC.Core.Settings.UserPreferences
        {
            PreferencesVersion = 4,
            DashboardMetrics = [MyLianPC.Core.Sensors.Metrics.CpuTemp, MyLianPC.Core.Sensors.Metrics.CoolantTemp, MyLianPC.Core.Sensors.Metrics.PumpRpm],
        }.Normalized();

        Assert.Equal(5, preferences.PreferencesVersion);
        Assert.Contains(MyLianPC.Core.Sensors.Metrics.CpuTemp, preferences.DashboardMetrics);
        Assert.DoesNotContain(MyLianPC.Core.Sensors.Metrics.CoolantTemp, preferences.DashboardMetrics);
        Assert.DoesNotContain(MyLianPC.Core.Sensors.Metrics.PumpRpm, preferences.DashboardMetrics);
        Assert.Contains(MyLianPC.Core.Sensors.Metrics.GpuVramUsed, preferences.DashboardMetrics);
    }

    [Fact]
    public void The_cooler_can_still_be_turned_back_on_after_the_migration()
    {
        var preferences = new MyLianPC.Core.Settings.UserPreferences
        {
            PreferencesVersion = 5,
            DashboardMetrics = [MyLianPC.Core.Sensors.Metrics.CpuTemp, MyLianPC.Core.Sensors.Metrics.PumpRpm],
        }.Normalized();

        Assert.Equal(5, preferences.PreferencesVersion);
        Assert.Contains(MyLianPC.Core.Sensors.Metrics.PumpRpm, preferences.DashboardMetrics);
    }
}
