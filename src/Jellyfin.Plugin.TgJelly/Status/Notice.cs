namespace Jellyfin.Plugin.TgJelly.Status;

/// <summary>
/// One problem shown in the dashboard's Status section (spec §10.3 <c>status</c>). The text is plain text.
/// </summary>
/// <param name="Id">The stable ID; it changes when the problem recurs, so a dismissal doesn't hide a new occurrence.</param>
/// <param name="Kind">The kind: BackupRestored, LoadWarning, PublicUrlFlagged, UpdateCheckFailing or RemovedLibraries.</param>
/// <param name="Text">The text for people.</param>
public sealed record Notice(string Id, string Kind, string Text);
