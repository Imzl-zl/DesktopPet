using DesktopPet.Core.ImageGen;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>总结图目标解析（windows-imagegen-design.md §8）：引用解析 + 自动回退。</summary>
public class SummaryImageTargetResolverTests
{
    private static ImageConnection Conn(string id, params string[] models) => new(
        Id: id, Name: id, Family: "openai",
        BaseUrl: "https://x.test/v1", ApiKeyRef: "cred:" + id,
        Models: models);

    private static readonly List<ImageConnection> TwoConnections =
    [
        Conn("relay", "gpt-image-2", "grok-imagine-image"),
        Conn("google", "gemini-3.1-flash-image"),
    ];

    [Fact]
    public void Resolve_NoConnections_ReturnsNull()
        => Assert.Null(SummaryImageTargetResolver.Resolve([], null));

    [Fact]
    public void Resolve_EmptyRef_FallsBackToFirstConnectionFirstModel()
    {
        var target = SummaryImageTargetResolver.Resolve(TwoConnections, null);
        Assert.NotNull(target);
        Assert.Equal("relay", target!.Connection.Id);
        Assert.Equal("gpt-image-2", target.ModelId);
    }

    [Fact]
    public void Resolve_ExplicitRef_ReturnsTarget()
    {
        var target = SummaryImageTargetResolver.Resolve(TwoConnections, "google/gemini-3.1-flash-image");
        Assert.NotNull(target);
        Assert.Equal("google", target!.Connection.Id);
        Assert.Equal("gemini-3.1-flash-image", target.ModelId);
    }

    [Fact]
    public void Resolve_UnknownConnection_FallsBack()
    {
        var target = SummaryImageTargetResolver.Resolve(TwoConnections, "nope/gpt-image-2");
        Assert.NotNull(target);
        Assert.Equal("relay", target!.Connection.Id);
    }

    [Fact]
    public void Resolve_ModelNotInConnection_FallsBack()
    {
        // 引用指定了连接但不属于其白名单的模型 → 回退该连接首模型
        var target = SummaryImageTargetResolver.Resolve(TwoConnections, "google/gpt-image-2");
        Assert.NotNull(target);
        Assert.Equal("google", target!.Connection.Id);
        Assert.Equal("gemini-3.1-flash-image", target.ModelId);
    }

    [Fact]
    public void Resolve_MalformedRef_FallsBack()
    {
        var target = SummaryImageTargetResolver.Resolve(TwoConnections, "no-slash-here");
        Assert.NotNull(target);
        Assert.Equal("relay", target!.Connection.Id);
    }

    [Fact]
    public void Resolve_ConnectionWithoutModels_Skipped()
    {
        var list = new List<ImageConnection> { Conn("empty"), Conn("ok", "m1") };
        var target = SummaryImageTargetResolver.Resolve(list, null);
        Assert.NotNull(target);
        Assert.Equal("ok", target!.Connection.Id);
    }
}
