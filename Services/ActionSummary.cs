using System;
using System.Collections.Generic;
using System.Text;

namespace Duplify.Services;

public static class ActionSummary
{
    public const int DefaultMaxFailuresShown = 5;

    public static string Build(
        string resultLabel,
        int total,
        int succeeded,
        int skipped,
        IReadOnlyList<string>? failures,
        int maxFailuresShown = DefaultMaxFailuresShown)
    {
        var failureList = failures ?? Array.Empty<string>();

        var builder = new StringBuilder();
        builder.Append($"{resultLabel}: {succeeded} из {total}.");
        builder.Append($"\nПропущено: {skipped}.");
        builder.Append($"\nОшибок: {failureList.Count}.");

        if (failureList.Count > 0)
        {
            builder.Append("\n");
            int shown = Math.Min(failureList.Count, Math.Max(0, maxFailuresShown));

            for (int i = 0; i < shown; i++)
            {
                builder.Append("\n• ").Append(failureList[i]);
            }

            if (failureList.Count > shown)
            {
                builder.Append($"\nи ещё {failureList.Count - shown}");
            }
        }

        return builder.ToString();
    }
}
