using System.Text.Json;
using DesktopPet.Core.Slicing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 切片对照测试：断言 C# SpriteSlicer 与 windows/src/pet.ts 的 slice() 在
/// 同一批合成测试图上输出一致。测试图与期望 JSON 由 scripts/slice-reference
/// 生成（真跑 TS slice() 固化），本测试只解码 PNG 后断言。
/// </summary>
public class SpriteSlicerTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static (byte[] Rgba, int Width, int Height) DecodePng(string fileName)
    {
        using var image = Image.Load<Rgba32>(Path.Combine(FixturesDir, fileName));
        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return (rgba, image.Width, image.Height);
    }

    private static List<SliceRect> ToRectList(JsonElement clips, int index)
        => clips[index]
            .EnumerateArray()
            .Select(r => new SliceRect(
                r.GetProperty("x").GetInt32(),
                r.GetProperty("y").GetInt32(),
                r.GetProperty("w").GetInt32(),
                r.GetProperty("h").GetInt32()))
            .ToList();

    private static void AssertMatchesExpected(string pngName, List<List<SliceRect>> actual)
    {
        var expectedJson = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Path.Combine(FixturesDir, "slice-expected.json")));
        var entry = expectedJson.EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == pngName);
        var expectedClips = entry.GetProperty("clips");

        Assert.Equal(expectedClips.GetArrayLength(), actual.Count);
        for (var i = 0; i < expectedClips.GetArrayLength(); i++)
        {
            Assert.Equal(ToRectList(expectedClips, i), actual[i]);
        }
    }

    [Fact]
    public void Slice_MatchesTsReference_OnUniformGridSheet()
    {
        var (rgba, w, h) = DecodePng("grid-2x3.png");
        AssertMatchesExpected("grid-2x3.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_MatchesTsReference_OnRaggedSheet()
    {
        var (rgba, w, h) = DecodePng("ragged.png");
        AssertMatchesExpected("ragged.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_MatchesTsReference_OnAlphaThresholdBoundary()
    {
        var (rgba, w, h) = DecodePng("alpha-edge.png");
        AssertMatchesExpected("alpha-edge.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_MatchesTsReference_OnSingleRowSheet()
    {
        var (rgba, w, h) = DecodePng("single-row.png");
        AssertMatchesExpected("single-row.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_MatchesTsReference_OnTouchingFrames()
    {
        var (rgba, w, h) = DecodePng("touching.png");
        AssertMatchesExpected("touching.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_ReturnsEmpty_OnFullyTransparentImage()
    {
        var (rgba, w, h) = DecodePng("transparent.png");
        AssertMatchesExpected("transparent.png", SpriteSlicer.Slice(rgba, w, h));
    }

    [Fact]
    public void Slice_ReturnsEmpty_OnZeroDimensions()
    {
        Assert.Empty(SpriteSlicer.Slice(Array.Empty<byte>(), 0, 0));
    }
}
