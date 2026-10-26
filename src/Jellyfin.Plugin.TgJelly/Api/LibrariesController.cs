using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Library;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Lists the libraries for the library filter.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class LibrariesController : ControllerBase
{
    private readonly ILibraryAccess _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrariesController"/> class.
    /// </summary>
    /// <param name="library">The library.</param>
    public LibrariesController(ILibraryAccess library) => _library = library;

    /// <summary>
    /// Returns every library.
    /// </summary>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryDto>> GetLibraries()
        => _library.GetVirtualFolders().Select(l => new LibraryDto { Id = l.Id.ToString("N"), Name = l.Name, CollectionType = l.CollectionType }).ToList();
}
