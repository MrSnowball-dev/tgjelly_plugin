using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public class LibrariesControllerTests
{
    [Fact]
    public void ListsVirtualFolders()
    {
        var library = new FakeLibrary();
        var movies = Guid.NewGuid();
        var books = Guid.NewGuid();
        library.VirtualFolders.Add(new LibraryRef(movies, "Movies", "movies"));
        library.VirtualFolders.Add(new LibraryRef(books, "Books", null));

        var dtos = new LibrariesController(library).GetLibraries().Value!;

        Assert.Equal(
            [(movies.ToString("N"), "Movies", "movies"), (books.ToString("N"), "Books", (string?)null)],
            dtos.Select(d => (d.Id, d.Name, d.CollectionType)));
    }
}
