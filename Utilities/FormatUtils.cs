using System;

namespace Duplify.Utilities;

public static class FormatUtils
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 Б";
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} МБ";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} ГБ";
    }

    public static string PluralizeFiles(int count)
    {
        int rem100 = count % 100;
        int rem10 = count % 10;

        if (rem100 >= 11 && rem100 <= 19)
            return $"{count} файлов";

        if (rem10 == 1)
            return $"{count} файл";

        if (rem10 >= 2 && rem10 <= 4)
            return $"{count} файла";

        return $"{count} файлов";
    }

    public static string PluralizeGroups(int count)
    {
        int rem100 = count % 100;
        int rem10 = count % 10;

        if (rem100 >= 11 && rem100 <= 19)
            return $"{count} групп";

        if (rem10 == 1)
            return $"{count} группа";

        if (rem10 >= 2 && rem10 <= 4)
            return $"{count} группы";

        return $"{count} групп";
    }
}
