using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.Tests;

/// <summary>
/// A temporary folder that is deleted when the test ends, even if a test left part of it read-only.
/// </summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgjelly-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var dir in Directory.GetDirectories(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: cleanup problems must not hide the real test result.
        }
    }
}
