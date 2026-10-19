using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The failed logins of one user in a sliding 10-minute window (spec §7.5): one message, edited as attempts arrive.
/// </summary>
public sealed class SecurityWindow
{
    /// <summary>Gets or sets the user name as typed in the latest attempt.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets when the window's first attempt happened.</summary>
    public DateTimeOffset FirstAt { get; set; }

    /// <summary>Gets or sets when the window's latest attempt happened.</summary>
    public DateTimeOffset LastAt { get; set; }

    /// <summary>Gets or sets the number of attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the distinct IPs the attempts came from.</summary>
    public List<string> Ips { get; set; } = [];

    /// <summary>Gets or sets the latest attempt's IP.</summary>
    public string LastIp { get; set; } = string.Empty;

    /// <summary>Gets or sets the latest attempt's client.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets the latest attempt's device.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user was locked out.</summary>
    public bool Locked { get; set; }
}
