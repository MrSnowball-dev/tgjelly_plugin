using System.Diagnostics.CodeAnalysis;

// The state documents are plain JSON data holders: System.Text.Json replaces them wholesale on load,
// and the owning stores mutate them only under their own locks.
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "JSON state documents are replaced wholesale on load.",
    Scope = "namespaceanddescendants",
    Target = "~N:Jellyfin.Plugin.TgJelly.State.Documents")]
[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "JSON state documents are internal data holders.",
    Scope = "namespaceanddescendants",
    Target = "~N:Jellyfin.Plugin.TgJelly.State.Documents")]
