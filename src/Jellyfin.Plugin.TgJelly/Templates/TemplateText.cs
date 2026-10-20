using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Helpers for template text.
/// </summary>
public static class TemplateText
{
    /// <summary>
    /// Hashes template text, to tell whether it changed.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <returns>The first 16 lowercase hex characters of the SHA-256 of the UTF-8 text.</returns>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
}
