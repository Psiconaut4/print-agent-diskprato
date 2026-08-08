using PrintAgent.Core.Retry;

namespace PrintAgent.Core.Tests;

public class LocalPrintRetryPolicyTests
{
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 90)]
    [InlineData(3, 120)]
    [InlineData(4, 150)]
    [InlineData(5, 180)]
    public void NextDelay_follows_the_documented_schedule(int attemptNumber, int expectedSeconds)
    {
        var delay = LocalPrintRetryPolicy.NextDelay(attemptNumber);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void NextDelay_schedule_sums_to_roughly_ten_minutes()
    {
        var total = TimeSpan.Zero;
        for (var attempt = 1; attempt <= LocalPrintRetryPolicy.MaxAttempts; attempt++)
        {
            total += LocalPrintRetryPolicy.NextDelay(attempt);
        }

        Assert.Equal(TimeSpan.FromMinutes(10), total);
    }

    [Fact]
    public void NextDelay_is_zero_after_the_schedule_is_exhausted()
    {
        var delay = LocalPrintRetryPolicy.NextDelay(LocalPrintRetryPolicy.MaxAttempts + 1);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void NextDelay_throws_for_non_positive_attempt_numbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalPrintRetryPolicy.NextDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalPrintRetryPolicy.NextDelay(-1));
    }

    [Fact]
    public void NextDelay_is_strictly_increasing_across_the_schedule()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= LocalPrintRetryPolicy.MaxAttempts; attempt++)
        {
            var current = LocalPrintRetryPolicy.NextDelay(attempt);
            Assert.True(current > previous, $"Tentativa {attempt}: {current} deveria ser maior que {previous}.");
            previous = current;
        }
    }
}
