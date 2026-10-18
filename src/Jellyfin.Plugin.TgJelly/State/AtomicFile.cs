using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Crash-safe file writes: write a temp file in the same folder, flush it to disk, then rename over the target.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Atomically replaces <paramref name="path"/> with <paramref name="contents"/>.
    /// </summary>
    /// <param name="path">The target file.</param>
    /// <param name="contents">The new contents.</param>
    /// <param name="ownerOnly">When <c>true</c>, the file is created with mode 600 on Unix.</param>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Utf8NoBom.GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Moves an unreadable file aside as <c>name.bad-yyyyMMddHHmmss.json</c> so a fresh one can be written.
    /// </summary>
    /// <param name="path">The unreadable file.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The path the file was moved to.</returns>
    public static string Quarantine(string path, DateTimeOffset now)
    {
        var withoutExtension = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var target = withoutExtension + ".bad-" + now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".json";
        File.Move(path, target, overwrite: true);
        return target;
    }
}
