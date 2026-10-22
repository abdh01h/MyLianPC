using MyLianPC.Core.Profiles;
using MyLianPC.Lcd;

namespace MyLianPC.Core.Tests;

/// <summary>The LCD clock follows the profile's 12/24-hour choice; every theme that shows a clock uses it.</summary>
public class LcdClockTests
{
    private static readonly DateTime Afternoon = new(2026, 10, 22, 17, 4, 9);

    [Fact]
    public void Twenty_four_hour_has_no_AM_or_PM()
    {
        Assert.Equal("17:04", LcdRenderer.ClockText(Afternoon, twentyFourHour: true));
        Assert.Equal("17:04:09", LcdRenderer.ClockText(Afternoon, twentyFourHour: true, seconds: true));
    }

    [Fact]
    public void Twelve_hour_uses_AM_and_PM()
    {
        Assert.Equal("5:04 PM", LcdRenderer.ClockText(Afternoon, twentyFourHour: false));
        Assert.Equal("9:30 AM", LcdRenderer.ClockText(new DateTime(2026, 10, 22, 9, 30, 0), twentyFourHour: false));
        Assert.Equal("11:51:02 PM", LcdRenderer.ClockText(new DateTime(2026, 10, 22, 23, 51, 2), twentyFourHour: false, seconds: true));
    }

    [Fact]
    public void Midnight_and_noon_show_twelve()
    {
        Assert.Equal("12:00 AM", LcdRenderer.ClockText(new DateTime(2026, 10, 22, 0, 0, 0), twentyFourHour: false));
        Assert.Equal("12:05 PM", LcdRenderer.ClockText(new DateTime(2026, 10, 22, 12, 5, 0), twentyFourHour: false));
        Assert.Equal("00:00", LcdRenderer.ClockText(new DateTime(2026, 10, 22, 0, 0, 0), twentyFourHour: true));
    }

    [Fact]
    public void The_clock_choice_defaults_to_24_hour_and_is_stored_with_the_profile()
    {
        Assert.True(new LcdSettings().Clock24Hour);
        var p = new LcdSettings { Clock24Hour = false };
        Assert.False(p.Clock24Hour);
        Assert.Empty(p.Validate());
    }

    [Fact]
    public void The_five_HUD_themes_have_their_own_ids_and_render()
    {
        string[] ids = ["cyberpunk", "hud-toxic", "hud-synthwave", "hud-ice", "hud-ember"];
        foreach (var id in ids)
        {
            Assert.Contains(LcdRenderer.Themes, t => t.Id == id);
            foreach (var twelveHour in new[] { true, false })
            {
                using var bmp = LcdRenderer.Render(new LcdSettings { ThemeId = id, Clock24Hour = twelveHour }, LcdMetrics.Sample);
                Assert.Equal(LcdRenderer.Size, bmp.Width);
                Assert.InRange(LcdRenderer.EncodeJpeg(bmp).Length, 1000, 150_000);
            }
        }
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
