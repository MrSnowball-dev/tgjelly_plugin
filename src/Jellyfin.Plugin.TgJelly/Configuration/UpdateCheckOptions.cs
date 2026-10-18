using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Update-check settings (spec §7.6).
/// </summary>
public class UpdateCheckOptions
{
    /// <summary>Gets or sets a value indicating whether the update checker may run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets which releases are reported.</summary>
    [XmlIgnore]
    public UpdateCheckMode Mode { get; set; } = UpdateCheckMode.SameMajor;

    /// <summary>Gets or sets <see cref="Mode"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Mode))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string ModeXml
    {
        get => Mode.ToString();
        set => Mode = EnumText.Parse<UpdateCheckMode>(value);
    }
}
