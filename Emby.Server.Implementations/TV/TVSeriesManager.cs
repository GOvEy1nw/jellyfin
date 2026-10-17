#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Emby.Server.Implementations.TV
{
    public class TVSeriesManager : ITVSeriesManager
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _configurationManager;

        public TVSeriesManager(IUserDataManager userDataManager, ILibraryManager libraryManager, IServerConfigurationManager configurationManager)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _configurationManager = configurationManager;
        }

        public QueryResult<BaseItem> GetNextUp(NextUpQuery query, DtoOptions options)
        {
            var user = query.User;

            BaseItem[] parents;

            if (query.ParentId.HasValue)
            {
                var parent = _libraryManager.GetItemById(query.ParentId.Value);

                if (parent is not null)
                {
                    parents = [parent];
                }
                else
                {
                    parents = [];
                }
            }
            else
            {
                parents = _libraryManager.GetUserRootFolder().GetChildren(user, true)
                   .Where(i => i is Folder)
                   .Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes).Contains(i.Id))
                   .ToArray();
            }

            return GetNextUp(query, parents, options);
        }

        public QueryResult<BaseItem> GetNextUp(NextUpQuery request, BaseItem[] parentsFolders, DtoOptions options)
        {
            var user = request.User;

            string? presentationUniqueKey = null;
            if (!request.SeriesId.IsNullOrEmpty())
            {
                if (_libraryManager.GetItemById(request.SeriesId.Value) is Series series)
                {
                    presentationUniqueKey = GetUniqueSeriesKey(series);
                }
            }

            var itemQuery = new InternalItemsQuery(user) { DtoOptions = options };
            var nextUpSeriesKeys = _libraryManager.GetNextUpSeriesKeys(itemQuery, parentsFolders, request.NextUpDateCutoff);
            if (!string.IsNullOrEmpty(presentationUniqueKey))
            {
                nextUpSeriesKeys = nextUpSeriesKeys.Where(key => key == presentationUniqueKey).ToArray();
            }

            var movies = request.EnableMovies && request.EnableResumable && request.SeriesId.IsNullOrEmpty()
                ? GetResumableMovies(user, parentsFolders, options)
                : [];

            return GetNextUpBatched(request, user, nextUpSeriesKeys, itemQuery, movies);
        }

        private QueryResult<BaseItem> GetNextUpBatched(
            NextUpQuery request,
            User user,
            IReadOnlyList<string> seriesKeys,
            InternalItemsQuery query,
            IReadOnlyList<BaseItem> movies)
        {
            if (seriesKeys.Count == 0 && movies.Count == 0)
            {
                return new QueryResult<BaseItem>();
            }

            var includeSpecials = _configurationManager.Configuration.DisplaySpecialsWithinSeasons;
            var includeRewatching = request.EnableRewatching;

            var batchResult = seriesKeys.Count == 0
                ? new Dictionary<string, MediaBrowser.Controller.Persistence.NextUpEpisodeBatchResult>()
                : _libraryManager.GetNextUpEpisodesBatch(query, seriesKeys, includeSpecials, includeRewatching);

            var nextUpList = new List<(DateTime EffectiveDate, BaseItem Item)>();

            foreach (var seriesKey in seriesKeys)
            {
                if (!batchResult.TryGetValue(seriesKey, out var result))
                {
                    continue;
                }

                var nextEpisode = DetermineNextEpisode(result, user, includeSpecials, request.EnableResumable, false);

                if (nextEpisode is not null)
                {
                    // The version that was actually played lives on the version item's user data.
                    // Scoped aggregate playback dates determine Next Up ordering.
                    var (playedVersion, _) = GetMostRecentlyPlayedVersion(result.LastWatched, user);
                    if (!ReferenceEquals(nextEpisode, result.ResumableNext))
                    {
                        nextEpisode = GetPreferredVersion(nextEpisode, result.LastWatched, playedVersion);
                    }

                    var lastWatchedDate = result.LastPlayedDate
                        ?? (result.LastWatched is null
                            ? DateTime.MinValue
                            : DateTime.MinValue.AddDays(1));
                    var boostDate = GetBoostDate(result);

                    nextUpList.Add((Max(lastWatchedDate, boostDate), nextEpisode));
                }

                if (nextEpisode is null && includeRewatching)
                {
                    var nextPlayedEpisode = DetermineNextEpisodeForRewatching(result, user, includeSpecials);

                    if (nextPlayedEpisode is not null)
                    {
                        var (playedVersion, _) = GetMostRecentlyPlayedVersion(result.LastWatchedForRewatching, user);
                        nextPlayedEpisode = GetPreferredVersion(nextPlayedEpisode, result.LastWatchedForRewatching, playedVersion);

                        var rewatchLastWatchedDate = result.LastPlayedDate
                            ?? (result.LastWatchedForRewatching is null
                                ? DateTime.MinValue
                                : DateTime.MinValue.AddDays(1));

                        nextUpList.Add((rewatchLastWatchedDate, nextPlayedEpisode));
                    }
                }
            }

            var movieData = _userDataManager.GetUserDataBatch(movies, user);
            foreach (var movie in movies)
            {
                nextUpList.Add((movieData.GetValueOrDefault(movie.Id)?.LastPlayedDate ?? DateTime.MinValue, movie));
            }

            var sortedItems = nextUpList
                .Where(x => x.EffectiveDate >= request.NextUpDateCutoff)
                .OrderByDescending(x => x.EffectiveDate)
                .ThenByDescending(x => x.Item.Id)
                .Select(x => x.Item);

            return GetResult(sortedItems, request);
        }

        private IReadOnlyList<BaseItem> GetResumableMovies(User user, BaseItem[] parentsFolders, DtoOptions dtoOptions)
        {
            return _libraryManager.GetItemList(
                new InternalItemsQuery(user)
                {
                    DtoOptions = dtoOptions,
                    IncludeItemTypes = [BaseItemKind.Movie],
                    IncludeOwnedItems = true,
                    IsResumable = true,
                    IsVirtualItem = false,
                    Recursive = true
                },
                parentsFolders.ToList());
        }

        private static DateTime? GetBoostDate(MediaBrowser.Controller.Persistence.NextUpEpisodeBatchResult result)
        {
            var addedDate = result.FirstNewEpisodeAddedDate;
            if (!addedDate.HasValue || addedDate.Value == DateTime.MinValue || addedDate.Value > DateTime.UtcNow || result.LastWatched is null)
            {
                return null;
            }

            return result.LastWatchedCompletedDate.HasValue && addedDate > result.LastWatchedCompletedDate ? addedDate : null;
        }

        private static DateTime Max(DateTime first, DateTime? second)
            => second.HasValue && second > first ? second.Value : first;

        private Episode? DetermineNextEpisode(
            MediaBrowser.Controller.Persistence.NextUpEpisodeBatchResult result,
            User user,
            bool includeSpecials,
            bool includeResumable,
            bool includePlayed)
        {
            var nextEpisode = (includePlayed ? result.NextPlayedForRewatching : result.NextUp) as Episode;
            var lastWatchedEpisode = (includePlayed ? result.LastWatchedForRewatching : result.LastWatched) as Episode;

            if (!includePlayed && includeResumable && result.ResumableNext is Episode resumableEpisode)
            {
                return resumableEpisode;
            }

            if (includeSpecials && result.Specials?.Count > 0)
            {
                var consideredEpisodes = result.Specials
                    .Cast<Episode>()
                    .Where(episode => episode.AirsBeforeSeasonNumber is not null || episode.AirsAfterSeasonNumber is not null)
                    .ToList();

                if (lastWatchedEpisode is not null)
                {
                    consideredEpisodes.Add(lastWatchedEpisode);
                }

                if (nextEpisode is not null)
                {
                    consideredEpisodes.Add(nextEpisode);
                }

                if (consideredEpisodes.Count > 0)
                {
                    var sortedEpisodes = _libraryManager.Sort(consideredEpisodes, user, [(ItemSortBy.AiredEpisodeOrder, SortOrder.Ascending)])
                        .Cast<Episode>();

                    if (lastWatchedEpisode is not null)
                    {
                        sortedEpisodes = sortedEpisodes.SkipWhile(episode => !episode.Id.Equals(lastWatchedEpisode.Id)).Skip(1);
                    }

                    if (!includePlayed)
                    {
                        sortedEpisodes = sortedEpisodes.Where(episode => _userDataManager.GetUserData(user, episode) is not { Played: true });
                    }

                    nextEpisode = sortedEpisodes.FirstOrDefault();
                }
            }

            if (nextEpisode is not null && !includeResumable)
            {
                // The resume progress may live on an alternate version
                foreach (var version in nextEpisode.GetAllVersions())
                {
                    if (_userDataManager.GetUserData(user, version)?.PlaybackPositionTicks > 0)
                    {
                        return null;
                    }
                }
            }

            return nextEpisode;
        }

        private Episode? DetermineNextEpisodeForRewatching(
            MediaBrowser.Controller.Persistence.NextUpEpisodeBatchResult result,
            User user,
            bool includeSpecials)
        {
            return DetermineNextEpisode(result, user, includeSpecials, includeResumable: false, includePlayed: true);
        }

        /// <summary>
        /// Gets the version of the last watched episode that was actually played, together with its last played date.
        /// The version that was played carries the most recent LastPlayedDate.
        /// dates.
        /// </summary>
        /// <param name="lastWatched">The last watched episode (any version).</param>
        /// <param name="user">The user.</param>
        /// <returns>The played version and its last played date.</returns>
        private (Video? PlayedVersion, DateTime? LastPlayedDate) GetMostRecentlyPlayedVersion(BaseItem? lastWatched, User user)
        {
            if (lastWatched is not Video lastWatchedVideo)
            {
                return (null, null);
            }

            var versions = lastWatchedVideo.GetAllVersions();
            var userDataByVersion = _userDataManager.GetUserDataBatch(versions, user);

            var playedVersion = VersionPlaybackSelector.SelectMostRecentlyPlayed(
                versions,
                version => userDataByVersion.GetValueOrDefault(version.Id),
                data => data.LastPlayedDate.HasValue);

            return (playedVersion, playedVersion is null ? null : userDataByVersion[playedVersion.Id].LastPlayedDate);
        }

        /// <summary>
        /// When the last watched episode was played as an alternate version, prefer the next episode's version with the matching name,
        /// so Next Up continues in the version the user has been watching instead of falling back to the primary.
        /// </summary>
        /// <param name="nextEpisode">The determined next episode (a primary).</param>
        /// <param name="lastWatched">The last watched episode.</param>
        /// <param name="playedVersion">The version of the last watched episode that was played.</param>
        /// <returns>The matching version of the next episode, or the episode itself.</returns>
        private Episode GetPreferredVersion(Episode nextEpisode, BaseItem? lastWatched, Video? playedVersion)
        {
            // No version preference, or the primary was played
            if (lastWatched is not Video lastWatchedVideo
                || playedVersion is null
                || !playedVersion.PrimaryVersionId.HasValue)
            {
                return nextEpisode;
            }

            // Match by version name
            var playedVersionId = playedVersion.Id.ToString("N", CultureInfo.InvariantCulture);
            var playedVersionName = lastWatchedVideo.GetMediaSources(false)
                .FirstOrDefault(source => string.Equals(source.Id, playedVersionId, StringComparison.OrdinalIgnoreCase))?.Name;

            if (string.IsNullOrEmpty(playedVersionName))
            {
                return nextEpisode;
            }

            var matchingSource = nextEpisode.GetMediaSources(false)
                .FirstOrDefault(source => string.Equals(source.Name, playedVersionName, StringComparison.OrdinalIgnoreCase));

            if (matchingSource is not null
                && Guid.TryParse(matchingSource.Id, out var matchingId)
                && !matchingId.Equals(nextEpisode.Id)
                && _libraryManager.GetItemById<Episode>(matchingId) is { } matchingVersion)
            {
                return matchingVersion;
            }

            return nextEpisode;
        }

        private static string GetUniqueSeriesKey(Series series)
        {
            return series.GetPresentationUniqueKey();
        }

        private static QueryResult<BaseItem> GetResult(IEnumerable<BaseItem> items, NextUpQuery query)
        {
            int totalCount = 0;

            if (query.EnableTotalRecordCount)
            {
                var list = items.ToList();
                totalCount = list.Count;
                items = list;
            }

            if (query.StartIndex.HasValue)
            {
                items = items.Skip(query.StartIndex.Value);
            }

            if (query.Limit.HasValue && query.Limit.Value > 0)
            {
                items = items.Take(query.Limit.Value);
            }

            return new QueryResult<BaseItem>(
                query.StartIndex,
                totalCount,
                items.ToArray());
        }
    }
}
