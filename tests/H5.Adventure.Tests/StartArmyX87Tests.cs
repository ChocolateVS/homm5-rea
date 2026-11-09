namespace H5.Adventure.Tests;

/// <summary>
/// FUN_00b513e0 under _PC_24 | _RC_CHOP (H5_Game.exe 0x009dc690). Expected counts come from an exact rational model of
/// the instruction sequence (every FMUL / FSUB / FADD chopped to 24 bits, FILD of a negative int + 2^32 chopped);
/// in every case above the range ends the old 53-bit, round-to-nearest-float version gave one more.
/// </summary>
public class StartArmyX87Tests
{
    [Theory]
    // rand32 ≥ 2^31: FILD loads it negative, the FADD 2^32 chops it to 24 bits (2863311533 → 2863311360).
    [InlineData(3, 1f, 2f, 2863311533u, 4)]
    [InlineData(6, 0.5f, 1.5f, 2863311532u, 6)]
    [InlineData(25, 3f, 4f, 2748779072u, 90)]
    // rand32 < 2^31: exact FILD, the chopped products / sums decide.
    [InlineData(9, 1.3f, 2.2f, 1219558712u, 13)]
    [InlineData(13, 1.3f, 2f, 991146523u, 18)]
    [InlineData(11, 0.3f, 1.6f, 510590473u, 4)]
    [InlineData(30, 0.1f, 1.4f, 2092419998u, 21)]
    [InlineData(40, 0.7f, 1.4f, 1533916990u, 37)]
    // Ends of the range.
    [InlineData(10, 1f, 2f, 0u, 10)]
    [InlineData(10, 1f, 2f, 4294967295u, 19)]
    public void RollIsChoppedLikeTheExe(int growth, float min, float max, uint rand32, int expected)
        => Assert.Equal(expected, Engine.Adventure.StartArmyCount(growth, min, max, rand32));
}
