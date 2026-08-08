namespace PrintAgent.Transport.Tests;

public class PairingCodeNormalizerTests
{
    [Theory]
    [InlineData("7k2m-9qxb", "7K2M9QXB")]
    [InlineData(" 7K2M 9QXB ", "7K2M9QXB")]
    [InlineData("7O2M9QXB", "702M9QXB")] // O -> 0
    [InlineData("7K2MIQXB", "7K2M1QXB")] // I -> 1
    [InlineData("oi-oi-oi", "010101")]
    public void Normalize_UppercasesStripsSeparatorsAndMapsAmbiguousLetters(string raw, string expected)
    {
        Assert.Equal(expected, PairingCodeNormalizer.Normalize(raw));
    }
}
