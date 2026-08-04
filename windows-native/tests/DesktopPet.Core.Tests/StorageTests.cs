using DesktopPet.Core.Storage;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 存储语义 1:1 移植自 windows/src-tauri/src/lib.rs（pet-positions.json /
/// pets-visible / 默认排布 / valid_pet_id / 12 上限）。
/// </summary>
public class StorageTests
{
    [Fact]
    public void ParsePositions_ReturnsEmpty_OnNullOrMalformedJson()
    {
        Assert.Empty(PetPositionsFile.Parse(null));
        Assert.Empty(PetPositionsFile.Parse(""));
        Assert.Empty(PetPositionsFile.Parse("not json"));
        Assert.Empty(PetPositionsFile.Parse("{}"));
        Assert.Empty(PetPositionsFile.Parse("{\"pet-a\": \"not-an-array\"}"));
        Assert.Empty(PetPositionsFile.Parse("{\"pet-a\": [1]}"));
    }

    [Fact]
    public void ParsePositions_ReadsIdToPointPairs()
    {
        var positions = PetPositionsFile.Parse("""{"pet-a": [100, 200], "pet-b": [-5, 40]}""");

        Assert.Equal(2, positions.Count);
        Assert.Equal(new PetPosition(100, 200), positions["pet-a"]);
        Assert.Equal(new PetPosition(-5, 40), positions["pet-b"]);
    }

    [Fact]
    public void SerializePositions_RoundTrips()
    {
        var positions = new Dictionary<string, PetPosition>
        {
            ["pet-a"] = new(100, 200),
            ["pet-b"] = new(-5, 40),
        };

        var json = PetPositionsFile.Serialize(positions);
        var reparsed = PetPositionsFile.Parse(json);

        Assert.Equal(positions, reparsed);
    }

    [Fact]
    public void UpdatePositions_IsImmutableAndReplacesPerId()
    {
        var positions = new Dictionary<string, PetPosition> { ["pet-a"] = new(1, 2) };

        var updated = PetPositionsFile.Update(positions, "pet-a", new PetPosition(3, 4));

        Assert.Equal(new PetPosition(1, 2), positions["pet-a"]); // 原字典不变
        Assert.Equal(new PetPosition(3, 4), updated["pet-a"]);
    }

    [Fact]
    public void DesktopPetsVisible_ParsesZeroAsHiddenAndEverythingElseAsVisible()
    {
        Assert.False(PetVisibility.Parse("0"));
        Assert.True(PetVisibility.Parse(null));
        Assert.True(PetVisibility.Parse(""));
        Assert.True(PetVisibility.Parse("1"));
        Assert.True(PetVisibility.Parse("true"));
        Assert.True(PetVisibility.Parse(" 1 ")); // trim 后非 "0" → 可见
        Assert.False(PetVisibility.Parse(" 0 ")); // trim 后为 "0" → 隐藏
    }

    [Fact]
    public void DefaultPetPosition_StacksFromBottomRightCorner()
    {
        var (x0, y0) = WindowPlacement.DefaultPetPosition(1920, 1080, 0);
        var (x1, y1) = WindowPlacement.DefaultPetPosition(1920, 1080, 1);

        Assert.Equal(1920 - 280, x0);
        Assert.Equal(1080 - 380, y0);
        Assert.Equal(1920 - 280 - 48, x1);
        Assert.Equal(1080 - 380 + 32, y1);
    }

    [Fact]
    public void DefaultPetPosition_ClampsToMinimum20()
    {
        var (x, y) = WindowPlacement.DefaultPetPosition(100, 100, 5);

        Assert.Equal(20, x);
        Assert.Equal(20, y);
    }

    [Theory]
    [InlineData("pet-a", true)]
    [InlineData("legacy-pet", true)]
    [InlineData("a1-b2", true)]
    [InlineData("", false)]
    [InlineData("PET-A", false)]
    [InlineData("pet a", false)]
    [InlineData("pet_a", false)]
    [InlineData("pet.1", false)]
    public void IsValidPetId_MatchesRustRule(string id, bool expected)
    {
        Assert.Equal(expected, WindowPlacement.IsValidPetId(id));
    }

    [Fact]
    public void MaxDesktopPets_IsTwelve()
    {
        Assert.Equal(12, WindowPlacement.MaxDesktopPets);
    }
}
