namespace PrintAgent.Transport.Tests;

public class RetryBackoffCalculatorTests
{
    [Fact]
    public void Next_DoublesUntilCap_WithNoJitter()
    {
        var calc = new RetryBackoffCalculator(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(60),
            jitterRatio: 0.2,
            randomSource: () => 0.5); // jitterFactor = 1.0 (sem desvio)

        Assert.Equal(TimeSpan.FromSeconds(1), calc.Next(1));
        Assert.Equal(TimeSpan.FromSeconds(2), calc.Next(2));
        Assert.Equal(TimeSpan.FromSeconds(4), calc.Next(3));
        Assert.Equal(TimeSpan.FromSeconds(8), calc.Next(4));
        Assert.Equal(TimeSpan.FromSeconds(60), calc.Next(10)); // teto
    }

    [Theory]
    [InlineData(0.0)] // extremo inferior: -20%
    [InlineData(1.0)] // extremo superior: +20%
    public void Next_AppliesJitterWithinConfiguredRatio(double randomValue)
    {
        var calc = new RetryBackoffCalculator(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(60),
            jitterRatio: 0.2,
            randomSource: () => randomValue);

        var delay = calc.Next(1);

        Assert.InRange(delay.TotalMilliseconds, 800, 1200);
    }
}
