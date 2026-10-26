using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Proxy settings without credentials.
/// </summary>
public class ProxySettings
{
    /// <summary>
    /// Gets or sets the proxy mode.
    /// </summary>
    [XmlIgnore]
    public ProxyMode Mode { get; set; } = ProxyMode.System;

    /// <summary>Gets or sets <see cref="Mode"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Mode))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string ModeXml
    {
        get => Mode.ToString();
        set => Mode = EnumText.Parse<ProxyMode>(value);
    }

    /// <summary>
    /// Gets or sets the proxy URL for <see cref="ProxyMode.Http"/> and <see cref="ProxyMode.Socks5"/>.
    /// </summary>
    public string Url { get; set; } = string.Empty;
}
