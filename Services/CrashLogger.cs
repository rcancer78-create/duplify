using System;
using System.IO;
using System.Text;
using Duplify.Utilities;

namespace Duplify.Services;

public static class CrashLogger
{
    public static string LogsDirectory => AppInfo.LogsDirectory;

    public static string LogException(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);

            string fileName = $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log";
            string filePath = Path.Combine(LogsDirectory, fileName);

            var builder = new StringBuilder();
            builder.AppendLine("Duplify crash report");
            builder.AppendLine($"TimeUtc: {DateTime.UtcNow:o}");
            builder.AppendLine($"Source: {source}");
            AppendException(builder, exception, 0);

            File.WriteAllText(filePath, builder.ToString());
            return filePath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendException(StringBuilder builder, Exception? exception, int depth)
    {
        if (exception == null || depth > 8)
        {
            return;
        }

        string indent = new string(' ', depth * 2);
        builder.AppendLine($"{indent}Type: {exception.GetType().FullName}");
        builder.AppendLine($"{indent}Message: {exception.Message}");
        builder.AppendLine($"{indent}StackTrace: {exception.StackTrace}");

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                AppendException(builder, inner, depth + 1);
            }

            return;
        }

        if (exception.InnerException != null)
        {
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }
}
