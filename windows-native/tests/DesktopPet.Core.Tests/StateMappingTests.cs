using DesktopPet.Core.Pets;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 1:1 移植自 windows/src/pet.ts 的 STATE_ROW / STATE_FPS / setState 行覆盖语义。
/// </summary>
public class StateMappingTests
{
    [Theory]
    [InlineData("idle", 0, 3)]
    [InlineData("registered", 0, 3)]
    [InlineData("working", 7, 8)]
    [InlineData("waiting", 6, 4)]
    [InlineData("done", 3, 3)]
    [InlineData("celebrate", 4, 8)]
    public void RowAndFps_MatchTsStateMaps(string state, int expectedRow, double expectedFps)
    {
        Assert.Equal(expectedRow, StateMapping.RowFor(state));
        Assert.Equal(expectedFps, StateMapping.FpsFor(state));
    }

    [Fact]
    public void UnknownState_FallsBackToRow0And3Fps()
    {
        Assert.Equal(0, StateMapping.RowFor("unknown-state"));
        Assert.Equal(3, StateMapping.FpsFor("unknown-state"));
    }

    [Fact]
    public void UserBinding_OverridesTheDefaultRow()
    {
        Assert.Equal(5, StateMapping.RowFor("idle", boundRow: 5));
        Assert.Equal(0, StateMapping.RowFor("idle", boundRow: 0));
    }

    [Fact]
    public void InvalidBinding_KeepsTheDefaultRow()
    {
        Assert.Equal(0, StateMapping.RowFor("idle", boundRow: -1));
        Assert.Equal(0, StateMapping.RowFor("idle", boundRow: null));
    }
}
