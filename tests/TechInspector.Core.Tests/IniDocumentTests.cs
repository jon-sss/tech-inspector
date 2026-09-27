using System.Text;
using TechInspector.Core.Ini;
using Xunit;

namespace TechInspector.Core.Tests;

public class IniDocumentTests
{
    // Lines copied in the shape iRacing writes them: app.ini pads to column 40, renderer files to 48.
    private const string AppSample =
        "\r\n" +
        "[Adaptive]\r\n" +
        "highContrastCursor=0                    \t; Set to 1 to use a high contrast mouse cursor\r\n" +
        "raceLineWidth=0.600000                  \t; Width of the race line in meters\r\n" +
        "\r\n" +
        "[Audio]\r\n" +
        "devSpeakerName=Speakers (Sound Blaster X4)\t; Name of the sound device\r\n" +
        "loudnessEngine=0                        \t; Volume adjustment for engines in dB\r\n" +
        "\r\n" +
        "[AutoLoadHUD]\r\n" +
        "global=Baseline\r\n";

    private const string RendererSample =
        "[Graphics Options]\r\n" +
        "LimitFrameRate=1                                \t; 0=no limit, 1=use DesiredFPSLimit\r\n" +
        "DesiredFPSLimit=100                             \t; fps\r\n";

    private static IniDocument Parse(string s) => IniDocument.Parse(Encoding.Latin1.GetBytes(s));
    private static string Text(IniDocument d) => Encoding.Latin1.GetString(d.ToBytes());

    [Fact]
    public void Unchanged_document_writes_back_identically()
    {
        Assert.Equal(AppSample, Text(Parse(AppSample)));
        Assert.Equal(RendererSample, Text(Parse(RendererSample)));
    }

    [Theory]
    [InlineData("a=1\nb=2\n")]            // LF only
    [InlineData("a=1\r\nb=2")]            // no final line ending
    [InlineData("")]                      // empty file
    [InlineData("; comment\r\n[x]\r\nk=v=w\t; note\r\n")] // '=' inside a value
    [InlineData("[cars\\acuransxevo22gt3]\r\nLastCarSet=\r\n")] // camera.ini shape
    [InlineData("daytona\\2011\\road=4,0.86,0.89\t; \r\n")]    // fueldata.ini shape
    public void Odd_shapes_round_trip(string s) => Assert.Equal(s, Text(Parse(s)));

    [Fact]
    public void A_hand_edited_line_padded_with_tabs_reads_its_value_without_the_tabs()
    {
        var d = Parse("[Graphics Options]\r\nHideCockpit=1\t\t\t\t\t\t; Test\r\n");
        var line = d.Find("Graphics Options", "HideCockpit")!;
        Assert.Equal("1", line.Value);
        Assert.Equal(" Test", line.Comment);
        Assert.True(line.HandPadded);
        d.TrySet("Graphics Options", "HideCockpit", "0");
        Assert.Contains("HideCockpit=0", Text(d));
    }

    [Fact]
    public void Learns_the_comment_column_per_file()
    {
        Assert.Equal(40, Parse(AppSample).CommentColumn);
        Assert.Equal(48, Parse(RendererSample).CommentColumn);
    }

    [Fact]
    public void Reads_key_value_comment_and_section()
    {
        var d = Parse(AppSample);
        var line = d.Find("Audio", "loudnessEngine")!;
        Assert.Equal("0", line.Value);
        Assert.Equal(" Volume adjustment for engines in dB", line.Comment);
        Assert.Equal("Audio", line.Section);
        Assert.Equal("Speakers (Sound Blaster X4)", d.Find("audio", "DEVSPEAKERNAME")!.Value);
        Assert.Equal("Baseline", d.Find("AutoLoadHUD", "global")!.Value);
    }

    [Fact]
    public void Setting_a_value_changes_only_that_line_and_keeps_the_column()
    {
        var d = Parse(AppSample);
        Assert.True(d.TrySet("Audio", "loudnessEngine", "-6"));
        var expected = AppSample.Replace(
            "loudnessEngine=0                        \t;",
            "loudnessEngine=-6                       \t;");
        Assert.Equal(expected, Text(d));
        Assert.True(d.IsDirty);
    }

    [Fact]
    public void A_value_longer_than_the_column_gets_no_padding_and_a_short_one_gets_padding_back()
    {
        var d = Parse(RendererSample);
        d.TrySet("Graphics Options", "DesiredFPSLimit", new string('9', 50));
        Assert.Contains("DesiredFPSLimit=" + new string('9', 50) + "\t; fps", Text(d));

        // devSpeakerName overflowed column 40 when loaded; a short name pads to the file's column.
        var a = Parse(AppSample);
        a.TrySet("Audio", "devSpeakerName", "X4");
        Assert.Contains("devSpeakerName=X4                       \t; Name of the sound device", Text(a));
    }

    [Fact]
    public void Setting_the_original_value_again_restores_the_original_text()
    {
        var d = Parse(AppSample);
        d.TrySet("Audio", "loudnessEngine", "-6");
        d.TrySet("Audio", "loudnessEngine", "0");
        Assert.False(d.IsDirty);
        Assert.Equal(AppSample, Text(d));
    }

    [Fact]
    public void Missing_keys_are_never_added()
    {
        var d = Parse(AppSample);
        Assert.False(d.TrySet("Audio", "noSuchKey", "1"));
        Assert.False(d.TrySet("NoSuchSection", "loudnessEngine", "1"));
        Assert.Equal(AppSample, Text(d));
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("x\t; injected")]
    public void Values_that_would_break_the_line_are_refused(string bad)
    {
        var d = Parse(AppSample);
        Assert.Throws<ArgumentException>(() => d.TrySet("Audio", "loudnessEngine", bad));
    }

    [Fact]
    public void Non_ascii_text_is_stored_as_utf8_and_read_back()
    {
        var d = Parse(AppSample);
        d.TrySet("Audio", "devSpeakerName", "Lautsprecher (Grüße)");
        var again = IniDocument.Parse(d.ToBytes());
        Assert.Equal("Lautsprecher (Grüße)", again.Find("Audio", "devSpeakerName")!.DisplayValue);
    }
}
