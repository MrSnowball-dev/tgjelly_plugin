using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Templates;

namespace Jellyfin.Plugin.TgJelly.Status;

/// <summary>
/// Computes the dashboard's notices from state and settings, and remembers which ones the admin dismissed.
/// </summary>
public sealed class NoticeBuilder
{
    /// <summary>The most dismissed IDs kept; the oldest is dropped beyond this.</summary>
    public const int MaxDismissed = 50;

    private readonly StateStore _state;
    private readonly SecretsStore _secrets;
    private readonly SettingsStore _settings;
    private readonly ILibraryAccess _library;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;
    private readonly TemplateFlags _templates;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoticeBuilder"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secrets store, for its load warning.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="library">The library, for removed-library filters.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="time">The clock.</param>
    /// <param name="templates">The template flags.</param>
    public NoticeBuilder(StateStore state, SecretsStore secrets, SettingsStore settings, ILibraryAccess library, StringTable strings, TimeProvider time, TemplateFlags templates)
    {
        _templates = templates;
        _state = state;
        _secrets = secrets;
        _settings = settings;
        _library = library;
        _strings = strings;
        _time = time;
    }

    /// <summary>
    /// Returns every current notice, dismissed ones included.
    /// </summary>
    /// <returns>The notices.</returns>
    public IReadOnlyList<Notice> Active()
    {
        var notices = new List<Notice>();
        var (backup, warnings, flag, failures, lastError) = _state.Read(s => (
            s.Status.BackupRestoredAt,
            s.Status.LoadWarnings.ToArray(),
            s.Status.PublicUrlFlag is { } f ? (f.Url, f.At) : ((string Url, DateTimeOffset At)?)null,
            s.UpdateCheck.ConsecutiveFailures,
            s.UpdateCheck.LastError));

        if (backup is { } restored)
        {
            var time = restored.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            notices.Add(new Notice("backup:" + restored.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "BackupRestored", _strings.Get("notice.backupRestored", ("time", time))));
        }

        foreach (var warning in warnings.Concat(_secrets.LoadWarning is { } w ? new[] { w } : []))
        {
            notices.Add(new Notice("load:" + Hash(warning), "LoadWarning", warning));
        }

        if (flag is { } rejected)
        {
            notices.Add(new Notice("publicUrl:" + rejected.At.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "PublicUrlFlagged", _strings.Get("notice.publicUrlFlagged", ("url", rejected.Url))));
        }

        if (failures >= 3)
        {
            notices.Add(new Notice("update:" + Hash(lastError ?? string.Empty), "UpdateCheckFailing", _strings.Get("update.failing", ("reason", lastError))));
        }

        var existing = _library.GetVirtualFolders().Select(l => l.Id).ToHashSet();
        var affected = _settings.Current.Destinations.Where(d => LibraryFilter.RefersOnlyToRemoved(d, existing)).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
        if (affected.Length > 0)
        {
            notices.Add(new Notice("libraries:" + Hash(string.Join(',', affected)), "RemovedLibraries", _strings.Plural("notice.removedLibraries", affected.Length)));
        }

        foreach (var key in _templates.Keys())
        {
            if (_templates.Current(key) is not { } templateFlag)
            {
                continue;
            }

            var id = "template:" + key + ":" + templateFlag.At.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var label = TemplateKeys.FullLabel(_strings, key);
            var text = templateFlag.Kind == "error"
                ? _strings.Get("notice.templateError", ("event", label), ("error", templateFlag.Error))
                : _strings.Get("notice.templateTruncated", ("event", label));
            notices.Add(new Notice(id, "TemplateFlagged", text));
        }

        return notices;
    }

    /// <summary>
    /// Returns the current notices that weren't dismissed, and forgets dismissals of notices that went away.
    /// </summary>
    /// <returns>The notices to show.</returns>
    public IReadOnlyList<Notice> Visible()
    {
        var active = Active();
        var ids = active.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var dismissed = _state.Read(s => s.Status.DismissedNotices.ToArray());
        if (dismissed.Any(id => !ids.Contains(id)))
        {
            _state.Update(s => s.Status.DismissedNotices.RemoveAll(id => !ids.Contains(id)));
            dismissed = [.. dismissed.Where(ids.Contains)];
        }

        return [.. active.Where(n => !dismissed.Contains(n.Id))];
    }

    /// <summary>
    /// Dismisses a notice.
    /// </summary>
    /// <param name="id">The notice ID.</param>
    /// <returns>Whether the notice is currently active.</returns>
    public bool Dismiss(string id)
    {
        if (!Active().Any(n => n.Id == id))
        {
            return false;
        }

        _state.Update(s =>
        {
            var list = s.Status.DismissedNotices;
            if (list.Contains(id))
            {
                return;
            }

            list.Add(id);
            if (list.Count > MaxDismissed)
            {
                list.RemoveRange(0, list.Count - MaxDismissed);
            }
        });
        return true;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
