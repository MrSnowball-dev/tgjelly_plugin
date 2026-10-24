using System;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The TgJelly logo as a file in the data folder, so deliveries can upload it like any poster. Written from the
/// embedded image at startup and rewritten when its size differs.
/// </summary>
public sealed class LogoFile
{
    private const string Resource = "Jellyfin.Plugin.TgJelly.Logo.png";

    private readonly DataPaths _paths;
    private readonly ILogger<LogoFile> _logger;
    private readonly object _gate = new();
    private byte[]? _bytes;
    private string? _cacheKey;
    private string? _lastProblem;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogoFile"/> class.
    /// </summary>
    /// <param name="paths">The data folder.</param>
    /// <param name="logger">The logger.</param>
    public LogoFile(DataPaths paths, ILogger<LogoFile> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the key under which Telegram's file_id for the logo is cached: it changes with the image.</summary>
    public string CacheKey
    {
        get
        {
            lock (_gate)
            {
                return _cacheKey ??= "tgjelly-logo:" + Convert.ToHexStringLower(SHA256.HashData(Bytes()))[..16];
            }
        }
    }

    /// <summary>
    /// Makes sure the logo file exists with the right size.
    /// </summary>
    /// <returns>The file's path, or <c>null</c> when it cannot be written; the message then goes out as text only.</returns>
    public string? Ensure()
    {
        lock (_gate)
        {
            var path = _paths.LogoPath;
            try
            {
                var bytes = Bytes();
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != bytes.Length)
                {
                    Directory.CreateDirectory(_paths.Root);
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temp, bytes);
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

                _lastProblem = null;
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Logged once per distinct problem, not for every message.
                if (ex.Message != _lastProblem)
                {
                    _lastProblem = ex.Message;
                    _logger.LogWarning(ex, "TgJelly could not write the logo to {Path}, so Security and Server messages go out without their poster until the folder is writable: {Reason}", path, ex.Message);
                }

                return null;
            }
        }
    }

    private byte[] Bytes()
    {
        if (_bytes is not null)
        {
            return _bytes;
        }

        using var stream = typeof(LogoFile).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The logo is missing from the plugin; reinstall TgJelly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return _bytes = buffer.ToArray();
    }
}
