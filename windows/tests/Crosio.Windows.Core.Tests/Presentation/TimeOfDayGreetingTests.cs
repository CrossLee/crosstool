using Crosio.Windows.Core.Presentation;

namespace Crosio.Windows.Core.Tests.Presentation;

public sealed class TimeOfDayGreetingTests
{
    [Theory]
    [InlineData(0, "早上好")]
    [InlineData(11, "早上好")]
    [InlineData(12, "下午好")]
    [InlineData(17, "下午好")]
    [InlineData(18, "晚上好")]
    [InlineData(23, "晚上好")]
    public void UsesCurrentLocalHour(int hour, string expected)
    {
        Assert.Equal(expected, TimeOfDayGreeting.ForHour(hour));
    }
}
