using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Summary;

namespace DesktopPet.Core.Tests;

public class ScreenEventStoreTests
{
    [Fact]
    public void Path_UsesDiarySubdirectoryWithDateName()
    {
        var path = ScreenEventStore.Path(@"C:\data", new DateOnly(2026, 8, 11));
        Assert.EndsWith(@"diary\screen-2026-08-11.jsonl", path);
    }

    [Fact]
    public void ParseDateFromFileName_RoundTrips()
    {
        var day = ScreenEventStore.ParseDateFromFileName("screen-2026-08-11.jsonl");
        Assert.Equal(new DateOnly(2026, 8, 11), day);
    }

    [Theory]
    [InlineData("screen-2026-08-11.txt")]
    [InlineData("journal-2026-08-11.jsonl")]
    [InlineData("screen-not-a-date.jsonl")]
    [InlineData("diary.txt")]
    public void ParseDateFromFileName_RejectsForeignFiles(string name)
    {
        Assert.Null(ScreenEventStore.ParseDateFromFileName(name));
    }

    [Fact]
    public void JournalLine_RoundTripsScreenEvent()
    {
        // 与应用层 journal 相同的编解码选项（camelCase + 枚举字符串）
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };
        var original = new ScreenEvent(
            new DateTime(2026, 8, 11, 14, 3, 22),
            ScreenEventKind.Music,
            "用户在听歌");

        var line = JsonSerializer.Serialize(original, opts);
        Assert.Contains("\"kind\":\"Music\"", line); // 枚举存字符串（文件可读）

        var restored = JsonSerializer.Deserialize<ScreenEvent>(line, opts);
        Assert.NotNull(restored);
        Assert.Equal(original.Timestamp, restored!.Timestamp);
        Assert.Equal(original.Kind, restored.Kind);
        Assert.Equal(original.Summary, restored.Summary);
    }
}
