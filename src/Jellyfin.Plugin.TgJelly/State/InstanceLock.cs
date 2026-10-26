using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// An exclusive lock on <c>state.lock</c>, held while this plugin instance owns the data folder.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Tries to take the lock without waiting.
    /// </summary>
    /// <param name="path">The lock file path.</param>
    /// <param name="denied">
    /// Set when the lock could not even be attempted (e.g. the folder is not writable); <c>null</c> when the lock is
    /// simply held by another instance or was acquired.
    /// </param>
    /// <returns>The held lock, or <c>null</c> when it is busy or denied.</returns>
    public static InstanceLock? TryAcquire(string path, out Exception? denied)
    {
        denied = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            return new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex) when (IsLockedByAnotherHolder(ex))
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            denied = ex;
            return null;
        }
    }

    /// <summary>
    /// Tries to take the lock without waiting.
    /// </summary>
    /// <param name="path">The lock file path.</param>
    /// <returns>The held lock, or <c>null</c> when another instance holds it.</returns>
    public static InstanceLock? TryAcquire(string path) => TryAcquire(path, out _);

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    // EWOULDBLOCK on Linux (11) and macOS (35); sharing and lock violations on Windows.
    private static bool IsLockedByAnotherHolder(IOException ex)
        => ex.HResult is 11 or 35 or unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
