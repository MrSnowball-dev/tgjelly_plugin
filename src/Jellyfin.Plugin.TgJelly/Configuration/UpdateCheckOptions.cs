using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Update-check settings (spec §7.6; plan 9 design §7): which kinds of newer Jellyfin release are announced.
/// </summary>
public class UpdateCheckOptions
{
    private const string LegacyNote = "v0.4's mode, read once by PluginConfiguration.MigrateUpdateCheck. Use Fixes, NewReleases and NewMajor.";

    /// <summary>Gets or sets a value indicating whether the update checker may run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a fix for the running version is announced (10.11.1 to 10.11.2).</summary>
    public bool Fixes { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a new release with the same major number is announced (10.11 to 10.12).</summary>
    public bool NewReleases { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a new major version is announced (10.x to 12.0).</summary>
    public bool NewMajor { get; set; }

    /// <summary>Gets or sets v0.4's mode (migration source only); <c>null</c> once it has been read.</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UpdateCheckMode? Mode { get; set; }

    // XML: .NET 9's XmlSerializer skips [Obsolete] members, so the mode is read through a twin under its old element
    // name, and written back only while it is set, i.e. never once the migration has run.
#pragma warning disable CS0618 // migration source
    /// <summary>Gets or sets <see cref="Mode"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Mode))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? ModeXml
    {
        get => Mode?.ToString();
        set => Mode = value is null ? null : EnumText.Parse<UpdateCheckMode>(value);
    }

    /// <summary>Tells the XML serializer whether to write <see cref="ModeXml"/>.</summary>
    /// <returns><c>true</c> while the mode is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeModeXml() => Mode.HasValue;

    /// <summary>
    /// Reads v0.4's mode once: <see cref="UpdateCheckMode.SameMajor"/> becomes fixes and new releases,
    /// <see cref="UpdateCheckMode.AnyNewer"/> all three. A configuration with no mode keeps the defaults.
    /// </summary>
    public void MigrateMode()
    {
        if (Mode is { } mode)
        {
            Fixes = true;
            NewReleases = true;
            NewMajor = mode == UpdateCheckMode.AnyNewer;
            Mode = null;
        }
    }
#pragma warning restore CS0618
}
