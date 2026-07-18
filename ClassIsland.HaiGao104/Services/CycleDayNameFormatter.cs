namespace ClassIsland.HaiGao104.Services;

public static class CycleDayNameFormatter
{
    private static readonly string[] Digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];

    public static string GetName(int dayNumber) => $"周{ToChineseNumber(Math.Max(1, dayNumber))}";

    private static string ToChineseNumber(int value)
    {
        if (value < 10)
        {
            return Digits[value];
        }

        if (value < 20)
        {
            return $"十{(value == 10 ? "" : Digits[value % 10])}";
        }

        if (value < 100)
        {
            return $"{Digits[value / 10]}十{(value % 10 == 0 ? "" : Digits[value % 10])}";
        }

        if (value == 100)
        {
            return "一百";
        }

        return value.ToString();
    }
}
