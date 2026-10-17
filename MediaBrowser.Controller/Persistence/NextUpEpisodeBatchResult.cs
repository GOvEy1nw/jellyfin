using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Persistence;

/// <summary>
/// Result of a batched NextUp query for a single series.
/// </summary>
public sealed class NextUpEpisodeBatchResult
{
    /// <summary>
    /// Gets or sets the latest episode playback date for the requesting user.
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets the earliest creation date for a regular episode after the last watched episode.
    /// </summary>
    public DateTime? FirstNewEpisodeAddedDate { get; set; }

    /// <summary>
    /// Gets or sets the recorded playback date for the completed version of <see cref="LastWatched"/>.
    /// </summary>
    public DateTime? LastWatchedCompletedDate { get; set; }

    /// <summary>
    /// Gets or sets the last watched episode (highest season/episode that is played).
    /// </summary>
    public BaseItem? LastWatched { get; set; }

    /// <summary>
    /// Gets or sets the next unwatched episode after the last watched position.
    /// </summary>
    public BaseItem? NextUp { get; set; }

    /// <summary>
    /// Gets or sets the furthest in-progress regular episode after <see cref="LastWatched"/>.
    /// </summary>
    public BaseItem? ResumableNext { get; set; }

    /// <summary>
    /// Gets or sets specials that may air between episodes.
    /// Only populated when includeSpecials is true.
    /// </summary>
    public IReadOnlyList<BaseItem>? Specials { get; set; }

    /// <summary>
    /// Gets or sets the last watched episode for rewatching mode (most recently played).
    /// Only populated when includeWatchedForRewatching is true.
    /// </summary>
    public BaseItem? LastWatchedForRewatching { get; set; }

    /// <summary>
    /// Gets or sets the next played episode for rewatching mode.
    /// Only populated when includeWatchedForRewatching is true.
    /// </summary>
    public BaseItem? NextPlayedForRewatching { get; set; }
}
