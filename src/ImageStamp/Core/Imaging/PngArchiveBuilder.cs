using System.IO.Compression;

namespace ImageStamp.Core.Imaging;

/// <summary>
/// Packages composed PNG files into a ZIP archive.
/// </summary>
public static class PngArchiveBuilder
{
    public static byte[] Create(IEnumerable<PngArchiveEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using MemoryStream output = new MemoryStream();

        using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            HashSet<string> entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PngArchiveEntry entry in entries)
            {
                string entryName = GetUniqueEntryName(entry.BaseImageFileName, entryNames);
                ZipArchiveEntry zipEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                using Stream content = zipEntry.Open();
                content.Write(entry.Png);
            }
        }

        return output.ToArray();
    }

    private static string GetUniqueEntryName(string baseImageFileName, HashSet<string> entryNames)
    {
        string fileName = Path.GetFileNameWithoutExtension(baseImageFileName);
        string stem = string.IsNullOrWhiteSpace(fileName) ? "composition" : fileName;
        string candidate = stem + ".png";
        int suffix = 2;

        while (!entryNames.Add(candidate))
        {
            candidate = stem + "-" + suffix + ".png";
            suffix++;
        }

        return candidate;
    }
}

public sealed record PngArchiveEntry(string BaseImageFileName, byte[] Png);
