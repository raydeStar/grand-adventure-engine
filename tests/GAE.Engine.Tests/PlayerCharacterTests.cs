using GAE.Core.Models;

namespace GAE.Engine.Tests;

public class PlayerCharacterTests
{
    [Theory]
    [InlineData(10, 0)]
    [InlineData(12, 1)]
    [InlineData(14, 2)]
    [InlineData(8, -1)]
    [InlineData(6, -2)]
    [InlineData(20, 5)]
    [InlineData(1, -4)]   // edge: minimum stat 1 => (1-10)/2 = -4 (integer division floors toward zero in C#)
    public void GetStatModifier_ReturnsCorrectValue(int statValue, int expected)
    {
        Assert.Equal(expected, PlayerCharacter.GetStatModifier(statValue));
    }

    [Fact]
    public void GetModifier_ByName_ReturnsCorrectValues()
    {
        var player = new PlayerCharacter { Str = 16, Dex = 14, Con = 12, Int = 8, Wis = 10, Cha = 15 };
        Assert.Equal(3, player.GetModifier("str"));
        Assert.Equal(2, player.GetModifier("dex"));
        Assert.Equal(1, player.GetModifier("con"));
        Assert.Equal(-1, player.GetModifier("int"));
        Assert.Equal(0, player.GetModifier("wis"));
        Assert.Equal(2, player.GetModifier("cha"));
    }

    [Fact]
    public void Defense_IncludesArmorAndDex()
    {
        var player = new PlayerCharacter
        {
            Dex = 14,
            Equipment = new EquipmentLoadout
            {
                Armor = new InventoryItem { ArmorValue = 5 }
            }
        };
        // 10 + dex_mod(2) + armor(5) = 17
        Assert.Equal(17, player.Defense);
    }

    [Fact]
    public void Defense_WithoutArmor_UsesBaseAndDex()
    {
        var player = new PlayerCharacter { Dex = 12 };
        // 10 + dex_mod(1) + 0 = 11
        Assert.Equal(11, player.Defense);
    }

    [Fact]
    public void IsAlive_TrueWhenHpPositive()
    {
        var player = new PlayerCharacter { Hp = 1 };
        Assert.True(player.IsAlive);
    }

    [Fact]
    public void IsAlive_FalseWhenHpZero()
    {
        var player = new PlayerCharacter { Hp = 0 };
        Assert.False(player.IsAlive);
    }
}
