using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace Duplify.Services;

public interface IFileActionService
{
    bool TrySendToRecycleBin(string filePath, out string? error);
    bool DeletePermanently(string filePath, out string? error);
    bool MoveFile(string sourcePath, string destinationDirectory, out string finalPath, out string? error);
    void OpenInExplorer(string filePath);
    void OpenFile(string filePath);
    bool OpenJournal(string journalFilePath);
    bool OpenFolder(string folderPath);
}

public class FileActionService : IFileActionService
{
    private readonly IActionJournal? _journal;

    public FileActionService()
        : this(null)
    {
    }

    public FileActionService(IActionJournal? journal)
    {
        _journal = journal;
    }

    public bool TrySendToRecycleBin(string filePath, out string? error)
    {
        error = null;

        if (!File.Exists(filePath))
        {
            error = "Файл не существует";
            _journal?.Record("error", filePath, false, error, "recycle");
            return false;
        }

        try
        {
            // UIOption.OnlyErrorDialogs may show a dialog; callers must invoke this on the UI thread.
            FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _journal?.Record("delete_recycle", filePath, true, "Отправлено в Корзину");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _journal?.Record("error", filePath, false, $"Не удалось отправить в Корзину: {ex.Message}", "recycle");
            return false;
        }
    }

    public bool DeletePermanently(string filePath, out string? error)
    {
        error = null;

        if (!File.Exists(filePath))
        {
            error = "Файл не существует";
            _journal?.Record("error", filePath, false, error, "permanent");
            return false;
        }

        try
        {
            File.Delete(filePath);
            _journal?.Record("delete_permanent", filePath, true, "Удалено безвозвратно");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _journal?.Record("error", filePath, false, $"Не удалось удалить безвозвратно: {ex.Message}", "permanent");
            return false;
        }
    }

    public bool MoveFile(string sourcePath, string destinationDirectory, out string finalPath, out string? error)
    {
        finalPath = string.Empty;
        error = null;

        if (!File.Exists(sourcePath))
        {
            error = "Исходный файл не найден";
            _journal?.Record("error", sourcePath, false, error, "move");
            return false;
        }

        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string fileName = Path.GetFileName(sourcePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            int counter = 1;
            while (true)
            {
                string candidatePath = counter == 1
                    ? Path.Combine(destinationDirectory, fileName)
                    : Path.Combine(destinationDirectory, $"{nameWithoutExt} ({counter}){ext}");

                if (string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    counter = (counter == 1) ? 2 : counter + 1;
                    continue;
                }

                if (File.Exists(candidatePath))
                {
                    counter = (counter == 1) ? 2 : counter + 1;
                    continue;
                }

                try
                {
                    File.Move(sourcePath, candidatePath);
                    finalPath = candidatePath;
                    _journal?.Record("move", sourcePath, true, "Файл перемещён", candidatePath);
                    return true;
                }
                catch (IOException) when (File.Exists(candidatePath))
                {
                    counter = (counter == 1) ? 2 : counter + 1;
                    if (counter > 10000)
                    {
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Ошибка перемещения: {ex.Message}";
            _journal?.Record("error", sourcePath, false, error, "move");
            return false;
        }
    }

    public void OpenInExplorer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Ignore failure
            }
        }
    }

    public void OpenFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore failure
        }
    }

    public bool OpenFolder(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenJournal(string journalFilePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = journalFilePath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            // .jsonl may have no association on some systems: fall back to Notepad.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{journalFilePath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
