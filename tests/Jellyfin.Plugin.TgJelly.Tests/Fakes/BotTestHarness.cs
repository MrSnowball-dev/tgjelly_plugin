using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// The stores, clock, fake Bot API and connection most Telegram tests need, wired together over a temp folder.
/// </summary>
public sealed class BotTestHarness : IDisposable
{
    public const string AdminCanPost = """{"status":"administrator","user":{"id":123456789,"is_bot":true,"first_name":"b"},"can_be_edited":false,"can_manage_chat":true,"can_change_info":false,"can_post_messages":true,"can_edit_messages":true,"can_delete_messages":false,"can_invite_users":false,"can_restrict_members":false,"can_promote_members":false,"can_manage_video_chats":false,"is_anonymous":false,"can_post_stories":false,"can_edit_stories":false,"can_delete_stories":false}""";

    private readonly List<IDisposable> _disposables = [];

    public BotTestHarness()
    {
        Dir = new TempDir();
        Paths = new DataPaths(Dir.Path);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
        Host = new FakeConfigurationHost();
        State = new StateStore(Paths, Time, NullLogger<StateStore>.Instance);
        State.Load();
        Settings = new SettingsStore(Host, Paths, State, Time, NullLogger<SettingsStore>.Instance);
        Secrets = new SecretsStore(Paths, Time, NullLogger<SecretsStore>.Instance);
        Strings = StringTable.LoadEmbedded();
        Api = new FakeBotApi();
        Connection = new BotConnection();
    }

    public TempDir Dir { get; }

    public DataPaths Paths { get; }

    public FakeTimeProvider Time { get; }

    public FakeConfigurationHost Host { get; }

    public StateStore State { get; }

    public SettingsStore Settings { get; }

    public SecretsStore Secrets { get; }

    public StringTable Strings { get; }

    public FakeBotApi Api { get; }

    public BotConnection Connection { get; }

    public void Connect()
    {
        Connection.Attach(new TelegramSender(Api.CreateClient()), FakeBotApi.BotId, FakeBotApi.BotUsername);
        Connection.SetState(ConnectionState.Connected);
    }

    public Destination AddDestination(long chatId, ChatKind kind = ChatKind.Supergroup, int threadId = 0, PresetKind preset = PresetKind.Everything, string? id = null)
    {
        var destination = new Destination
        {
            Id = id ?? DestinationIds.New(Settings.Current.Destinations.Select(d => d.Id)),
            ChatId = chatId,
            ThreadId = threadId,
            Title = "Chat " + chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind = kind,
            Preset = preset,
            Events = EventCatalog.Expand(preset),
        };
        var result = Settings.Update(null, c => c.Destinations = [.. c.Destinations, destination]);
        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);

        var type = kind switch
        {
            ChatKind.Private => "private",
            ChatKind.Group => "group",
            ChatKind.Channel => "channel",
            _ => "supergroup",
        };
        Api.AddChat(chatId, type, destination.Title);
        if (kind != ChatKind.Private)
        {
            Api.SetMember(chatId, AdminCanPost);
        }

        return destination;
    }

    public async Task<InstanceGate> StartOwnedGateAsync()
    {
        var gate = new InstanceGate(Paths, Time, NullLogger<InstanceGate>.Instance);
        _disposables.Add(gate);
        await gate.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await gate.WaitForOwnershipAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return gate;
    }

    public void Track(IDisposable disposable) => _disposables.Add(disposable);

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            d.Dispose();
        }

        Settings.Dispose();
        Api.Dispose();
        Dir.Dispose();
    }
}
