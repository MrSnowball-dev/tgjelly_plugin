using System.IO;
using System.Runtime.Versioning;
using System;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class SecretsStoreTests
{
    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        using var dir = new TempDir();

        Assert.Equal(BotSecrets.Empty, NewStore(dir).Load());
    }

    [Fact]
    public void Save_ThenNewStore_ReadsSameValues_AsCamelCaseJson()
    {
        using var dir = new TempDir();
        NewStore(dir).Save(new BotSecrets { BotToken = "123456:ABC", ProxyUsername = "u", ProxyPassword = "p" });

        var loaded = NewStore(dir).Load();

        Assert.Equal("123456:ABC", loaded.BotToken);
        Assert.Equal("p", loaded.ProxyPassword);
        Assert.Contains("\"botToken\"", File.ReadAllText(new DataPaths(dir.Path).Secrets), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ChangesOneField_KeepsOthers()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.Save(new BotSecrets { BotToken = "t", ProxyPassword = "keep" });

        store.Update(s => s with { BotToken = string.Empty });

        Assert.Equal(string.Empty, NewStore(dir).Load().BotToken);
        Assert.Equal("keep", NewStore(dir).Load().ProxyPassword);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_AndTreatedAsEmpty()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.Secrets, "{oops");

        var store = NewStore(dir);
        var loaded = store.Load();

        Assert.Equal(BotSecrets.Empty, loaded);
        Assert.StartsWith("secrets.json was unreadable", store.LoadWarning, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.Secrets));
        Assert.Single(Directory.GetFiles(paths.Root, "secrets.bad-*.json"));
    }

    [Fact]
    public void Works_InAFolderWithSpacesAndNonAsciiCharacters()
    {
        // E.g. "C:\ProgramData\Jellyfin\Server" on Windows, or a localized home folder.
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "Jellyfin Данные ü");
        var store = new SecretsStore(new DataPaths(root), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance);

        store.Save(new BotSecrets { BotToken = "123:abc" });

        Assert.Equal("123:abc", new SecretsStore(new DataPaths(root), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance).Load().BotToken);
    }

    private static SecretsStore NewStore(TempDir dir)
        => new(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance);

    [Fact]
    public void ToString_NeverShowsTheSecrets()
    {
        var text = new BotSecrets { BotToken = "123456:ABCDEFsecret", ProxyPassword = "hunter2" }.ToString();

        Assert.DoesNotContain("ABCDEFsecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("HasBotToken = True", text, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void UnreadableFile_IsALoadWarning_NotAnException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        NewStore(dir).Save(new BotSecrets { BotToken = "t" });
        var path = new DataPaths(dir.Path).Secrets;
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = NewStore(dir);

            Assert.Equal(BotSecrets.Empty, store.Load());
            Assert.Contains("secrets.json", store.LoadWarning, StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void UnreadableFile_IsReadAgain_OnceReadable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        NewStore(dir).Save(new BotSecrets { BotToken = "t" });
        var path = new DataPaths(dir.Path).Secrets;
        var store = NewStore(dir);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            Assert.Equal(BotSecrets.Empty, store.Load());
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal("t", store.Load().BotToken);
        Assert.Null(store.LoadWarning);
    }
}
