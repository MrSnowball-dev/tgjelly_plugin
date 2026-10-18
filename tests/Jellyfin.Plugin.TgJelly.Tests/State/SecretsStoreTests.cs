using System;
using System.IO;
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
}
