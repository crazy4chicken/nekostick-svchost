using System.Text;

namespace Nekostick.ServiceHost.Logs;

/// <summary>Describes one page of a service's rotating log files.</summary>
public sealed record LogFilePage(
    int FileIndex,
    int FileCount,
    int LineCount,
    IReadOnlyList<string> Lines);

/// <summary>Reads one file from a service's rotating log history.</summary>
public sealed class ServiceLogReader
{
    private const int MaximumFileIndex = 4;

    /// <summary>Reads one existing file, returning null when the requested page is unavailable.</summary>
    public LogFilePage? ReadPage(string logDirectory, string serviceName, int fileIndex)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) ||
            !IsValidServiceName(serviceName) ||
            fileIndex is < 0 or > MaximumFileIndex)
        {
            return null;
        }

        var fileCount = 0;
        string? pagePath = null;
        for (var candidateIndex = 0; candidateIndex <= MaximumFileIndex; candidateIndex++)
        {
            var fileName = candidateIndex == 0
                ? $"{serviceName}.log"
                : $"{serviceName}.{candidateIndex}.log";
            var candidatePath = Path.Combine(logDirectory, fileName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            fileCount++;
            if (candidateIndex == fileIndex)
            {
                pagePath = candidatePath;
            }
        }

        if (pagePath is null)
        {
            return null;
        }

        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(
                pagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return new LogFilePage(fileIndex, fileCount, lines.Count, lines);
    }

    private static bool IsValidServiceName(string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName) ||
            serviceName.Length > 63 ||
            !IsNameCharacter(serviceName[0], allowHyphen: false))
        {
            return false;
        }

        for (var index = 1; index < serviceName.Length; index++)
        {
            if (!IsNameCharacter(serviceName[index], allowHyphen: true))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNameCharacter(char character, bool allowHyphen) =>
        character is >= 'a' and <= 'z' ||
        character is >= '0' and <= '9' ||
        allowHyphen && character == '-';
}
