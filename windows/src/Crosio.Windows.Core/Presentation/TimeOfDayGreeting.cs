namespace Crosio.Windows.Core.Presentation;

public static class TimeOfDayGreeting
{
    public static string ForHour(int localHour)
    {
        if (localHour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(localHour));
        }

        return localHour switch
        {
            < 12 => "早上好",
            < 18 => "下午好",
            _ => "晚上好",
        };
    }
}
