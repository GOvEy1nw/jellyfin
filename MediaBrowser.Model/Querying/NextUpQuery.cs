#pragma warning disable CS1591

using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.Querying;

public class NextUpQuery
{
    public NextUpQuery()
    {
        EnableImageTypes = Array.Empty<ImageType>();
        EnableTotalRecordCount = true;
        NextUpDateCutoff = DateTime.MinValue;
        EnableResumable = false;
        EnableRewatching = false;
        EnableMovies = true;
    }

    /// <summary>
    /// Gets or sets the user.
    /// </summary>
    /// <value>The user.</value>
    public required User User { get; set; }

    /// <summary>
    /// Gets or sets the parent identifier.
    /// </summary>
    /// <value>The parent identifier.</value>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    /// <value>The series id.</value>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the start index. Use for paging.
    /// </summary>
    /// <value>The start index.</value>
    public int? StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of items to return.
    /// </summary>
    /// <value>The limit.</value>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets the enable image types.
    /// </summary>
    /// <value>The enable image types.</value>
    public ImageType[] EnableImageTypes { get; set; }

    public bool EnableTotalRecordCount { get; set; }

    /// <summary>
    /// Gets or sets the minimum effective activity date for an item to appear in Next Up.
    /// </summary>
    public DateTime NextUpDateCutoff { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to include resumable episodes and movies as next up.
    /// </summary>
    public bool EnableResumable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether getting rewatching next up list.
    /// </summary>
    public bool EnableRewatching { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to include resumable movies in next up results.
    /// </summary>
    public bool EnableMovies { get; set; }
}
