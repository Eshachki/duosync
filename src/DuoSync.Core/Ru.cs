namespace DuoSync.Core;

/// <summary>Russian plural forms for counts shown to people.</summary>
public static class Ru
{
    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = Math.Abs(n) % 10, m100 = Math.Abs(n) % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    public static string Files(int n) => $"{n} {Plural(n, "файл", "файла", "файлов")}";
}
