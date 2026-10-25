namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// The value names of <see cref="NotificationEvent.Values"/>: the payload snapshot that layouts and templates
/// read. Stable strings.
/// </summary>
public static class EventFields
{
    /// <summary>The user name as typed or stored.</summary>
    public const string User = "user";

    /// <summary>The normalized user name (<c>ToUpperInvariant</c>).</summary>
    public const string UserKey = "user_key";

    /// <summary>The user ID (Guid <c>N</c>).</summary>
    public const string UserId = "user_id";

    /// <summary>The remote IP address.</summary>
    public const string Ip = "ip";

    /// <summary>The client app.</summary>
    public const string Client = "client";

    /// <summary>The device name.</summary>
    public const string Device = "device";

    /// <summary>The device ID.</summary>
    public const string DeviceId = "device_id";

    /// <summary>A number of attempts.</summary>
    public const string Attempts = "attempts";

    /// <summary>A plugin name.</summary>
    public const string Plugin = "plugin";

    /// <summary>A version.</summary>
    public const string Version = "version";

    /// <summary>A plugin changelog.</summary>
    public const string Changelog = "changelog";

    /// <summary>An error message.</summary>
    public const string Error = "error";

    /// <summary>A scheduled task name.</summary>
    public const string Task = "task";

    /// <summary>The server name.</summary>
    public const string Server = "server";

    /// <summary>The running version (update checks).</summary>
    public const string Current = "current";

    /// <summary>A URL (release notes).</summary>
    public const string Url = "url";

    /// <summary>How many distinct IPs (coalesced failed logins).</summary>
    public const string IpCount = "ip_count";

    /// <summary>Whether the account is locked (<c>true</c>/<c>false</c>).</summary>
    public const string Locked = "locked";
}
