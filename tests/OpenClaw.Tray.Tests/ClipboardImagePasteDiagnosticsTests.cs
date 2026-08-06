using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ClipboardImagePasteDiagnosticsTests
{
    [Fact]
    public void FormatAvailableFormats_ReturnsNoneForMissingFormats()
    {
        Assert.Equal("(none)", ClipboardImagePasteDiagnostics.FormatAvailableFormats(null));
        Assert.Equal("(none)", ClipboardImagePasteDiagnostics.FormatAvailableFormats([]));
    }

    [Fact]
    public void FormatAvailableFormats_BoundsAndSanitizesFormatIdentifiers()
    {
        var formats = Enumerable.Range(0, 26)
            .Select(index => index == 0
                ? "Bitmap\r\nInjected"
                : $"Format-{index}-{new string('x', 100)}")
            .ToArray();

        var result = ClipboardImagePasteDiagnostics.FormatAvailableFormats(formats);

        Assert.Contains("Bitmap??Injected", result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
        Assert.Contains("...", result);
        Assert.Contains("(+2 more)", result);
        Assert.DoesNotContain("Format-24-", result);
    }
}
