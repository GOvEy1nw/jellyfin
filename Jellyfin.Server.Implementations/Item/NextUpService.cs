#pragma warning disable RS0030 // Do not use banned APIs

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using BaseItemDto = MediaBrowser.Controller.Entities.BaseItem;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Provides next-up episode query operations.
/// </summary>
public class NextUpService : INextUpService
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly IItemQueryHelpers _queryHelpers;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextUpService"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="itemTypeLookup">The item type lookup.</param>
    /// <param name="queryHelpers">The shared query helpers.</param>
    public NextUpService(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IItemTypeLookup itemTypeLookup,
        IItemQueryHelpers queryHelpers)
    {
        _dbProvider = dbProvider;
        _itemTypeLookup = itemTypeLookup;
        _queryHelpers = queryHelpers;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery filter, DateTime dateCutoff)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.User);

        _ = dateCutoff;
        _queryHelpers.PrepareFilterQuery(filter);
        using var context = _dbProvider.CreateDbContext();

        var episodeBase = _queryHelpers.TranslateQuery(
            context.BaseItems
                .AsNoTracking()
                .Where(i => i.Type == _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode]),
            context,
            filter);

        var playedVersions = GetEpisodeVersions(context, filter)
            .Join(
                context.UserData.AsNoTracking().Where(e => e.UserId == filter.User.Id && e.ItemId != EF.Constant(BaseItemRepository.PlaceholderId) && (e.Played || e.PlaybackPositionTicks > 0)),
                version => version.Id,
                data => data.ItemId,
                (version, data) => new
                {
                    ItemId = version.PrimaryVersionId.HasValue && context.BaseItems.Any(primary => primary.Id == version.PrimaryVersionId && primary.TopParentId == version.TopParentId)
                        ? version.PrimaryVersionId.Value : version.Id,
                    version.TopParentId,
                    data.LastPlayedDate
                });
        var query = episodeBase
            .Join(
                playedVersions,
                episode => new { ItemId = episode.Id, episode.TopParentId },
                data => new { data.ItemId, data.TopParentId },
                (episode, data) => new { episode.SeriesPresentationUniqueKey, data.LastPlayedDate })
            .GroupBy(g => g.SeriesPresentationUniqueKey)
            .Select(g => new { g.Key, LastPlayedDate = g.Max(u => u.LastPlayedDate) })
            .Where(g => g.Key != null)
            .OrderByDescending(g => g.LastPlayedDate)
            .Select(g => g.Key!);

        if (filter.Limit.HasValue)
        {
            query = query.Take(filter.Limit.Value);
        }

        return query.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NextUpEpisodeBatchResult> GetNextUpEpisodesBatch(
        InternalItemsQuery filter,
        IReadOnlyList<string> seriesKeys,
        bool includeSpecials,
        bool includeWatchedForRewatching)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.User);

        if (seriesKeys.Count == 0)
        {
            return new Dictionary<string, NextUpEpisodeBatchResult>();
        }

        _queryHelpers.PrepareFilterQuery(filter);
        using var context = _dbProvider.CreateDbContext();

        var userId = filter.User.Id;
        var episodeTypeName = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode];

        var episodeBase = _queryHelpers.TranslateQuery(
            context.BaseItems
                .AsNoTracking()
                .Where(e => e.Type == episodeTypeName)
                .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey)),
            context,
            filter);

        var versionPlayback = GetEpisodeVersions(context, filter)
            .Where(e => e.SeriesPresentationUniqueKey != null && seriesKeys.Contains(e.SeriesPresentationUniqueKey))
            .Join(
                context.UserData
                    .AsNoTracking()
                    .Where(ud => ud.UserId == userId && ud.ItemId != EF.Constant(BaseItemRepository.PlaceholderId)),
                version => version.Id,
                data => data.ItemId,
                (version, data) => new
                {
                    ItemId = version.PrimaryVersionId.HasValue && context.BaseItems.Any(primary => primary.Id == version.PrimaryVersionId && primary.TopParentId == version.TopParentId)
                        ? version.PrimaryVersionId.Value : version.Id,
                    VersionId = version.Id,
                    version.TopParentId,
                    data.Played,
                    data.PlaybackPositionTicks,
                    data.LastPlayedDate
                });
        var latestPlayedBySeries = episodeBase
            .Join(
                versionPlayback,
                episode => new { ItemId = episode.Id, episode.TopParentId },
                data => new { data.ItemId, data.TopParentId },
                (episode, data) => new { episode.SeriesPresentationUniqueKey, data.LastPlayedDate })
            .GroupBy(e => e.SeriesPresentationUniqueKey)
            .Select(g => new { SeriesKey = g.Key, LastPlayedDate = g.Max(e => e.LastPlayedDate) })
            .ToDictionary(e => e.SeriesKey!, e => e.LastPlayedDate);

        var playedEpisodeIds = versionPlayback.Where(data => data.Played).Select(data => data.ItemId);
        var lastWatchedBase = episodeBase
            .Where(e => e.ParentIndexNumber != 0)
            .Where(e => playedEpisodeIds.Contains(e.Id));

        // Use lightweight projection + client-side dedup to avoid the correlated scalar subquery
        // per group that EF generates for GroupBy+OrderByDescending+FirstOrDefault.
        var allPlayedLite = lastWatchedBase
            .Select(e => new
            {
                e.Id,
                e.SeriesPresentationUniqueKey,
                e.ParentIndexNumber,
                e.IndexNumber
            })
            .ToList();

        var lastWatchedInfo = allPlayedLite
            .OrderByDescending(e => e.ParentIndexNumber)
            .ThenByDescending(e => e.IndexNumber)
            .DistinctBy(e => e.SeriesPresentationUniqueKey)
            .ToDictionary(e => e.SeriesPresentationUniqueKey!, e => e.Id);

        Dictionary<string, Guid> lastWatchedByDateInfo = new();
        if (includeWatchedForRewatching)
        {
            var lastWatchedByDateBase = episodeBase.Where(e => e.ParentIndexNumber != 0);

            // Use an explicit Join (INNER JOIN) instead of SelectMany on a collection navigation.
            // SelectMany on UserData with a correlated Where would translate to APPLY,
            // which SQLite does not support.
            // Access filtering leaves only primaries in the base query, but a play can be recorded
            // against any version, so each row is attributed to its group's primary before the join.
            var playedByGroupPrimary = versionPlayback.Where(data => data.Played);

            var playedWithDates = lastWatchedByDateBase
                .Join(
                    playedByGroupPrimary,
                    e => new { ItemId = e.Id, e.TopParentId },
                    ud => new { ud.ItemId, ud.TopParentId },
                    (e, ud) => new { EpisodeId = e.Id, e.SeriesPresentationUniqueKey, ud.LastPlayedDate })
                .ToList();

            lastWatchedByDateInfo = playedWithDates
                .OrderByDescending(x => x.LastPlayedDate)
                .DistinctBy(x => x.SeriesPresentationUniqueKey)
                .ToDictionary(x => x.SeriesPresentationUniqueKey!, x => x.EpisodeId);
        }

        var allLastWatchedIds = lastWatchedInfo.Values
            .Concat(lastWatchedByDateInfo.Values)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var completedLastWatchedDates = versionPlayback
            .Where(data => data.Played && data.LastPlayedDate.HasValue && allLastWatchedIds.Contains(data.ItemId))
            .GroupBy(item => item.ItemId)
            .Select(group => new { LastWatchedId = group.Key, LastPlayedDate = group.Max(item => item.LastPlayedDate) })
            .ToDictionary(item => item.LastWatchedId, item => item.LastPlayedDate);
        var lwQuery = context.BaseItems.AsNoTracking().Where(e => allLastWatchedIds.Contains(e.Id));
        lwQuery = _queryHelpers.ApplyNavigations(lwQuery, filter);
        var lastWatchedEpisodes = lwQuery.ToDictionary(e => e.Id);

        Dictionary<string, List<BaseItemEntity>> specialsBySeriesKey = new();
        if (includeSpecials)
        {
            var specialsQuery = episodeBase
                .Where(e => e.ParentIndexNumber == 0)
                .Where(e => !e.IsVirtualItem);
            specialsQuery = _queryHelpers.ApplyNavigations(specialsQuery, filter).AsSingleQuery();

            foreach (var special in specialsQuery)
            {
                var key = special.SeriesPresentationUniqueKey!;
                if (!specialsBySeriesKey.TryGetValue(key, out var list))
                {
                    list = new List<BaseItemEntity>();
                    specialsBySeriesKey[key] = list;
                }

                list.Add(special);
            }
        }

        var positionLookup = new Dictionary<string, (int Season, int Episode)>();
        foreach (var kvp in lastWatchedInfo)
        {
            if (kvp.Value != Guid.Empty
                && lastWatchedEpisodes.TryGetValue(kvp.Value, out var lw)
                && lw.ParentIndexNumber.HasValue
                && lw.IndexNumber.HasValue)
            {
                positionLookup[kvp.Key] = (lw.ParentIndexNumber.Value, lw.IndexNumber.Value);
            }
        }

        var allUnplayedBase = episodeBase
            .Where(e => e.ParentIndexNumber != 0)
            .Where(e => !e.IsVirtualItem)
            .Where(e => !playedEpisodeIds.Contains(e.Id));
        var allUnplayedCandidates = allUnplayedBase
            .Select(e => new
            {
                e.Id,
                e.SeriesPresentationUniqueKey,
                e.ParentIndexNumber,
                EpisodeNumber = e.IndexNumber
            })
            .ToList();

        var resumableCandidates = versionPlayback
            .Where(data => !data.Played && data.PlaybackPositionTicks > 0)
            .Join(
                episodeBase.Where(e => e.ParentIndexNumber != 0 && !e.IsVirtualItem),
                ud => new { ud.ItemId, ud.TopParentId },
                episode => new { ItemId = episode.Id, episode.TopParentId },
                (ud, episode) => new
                {
                    Id = ud.VersionId,
                    episode.SeriesPresentationUniqueKey,
                    episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    ud.LastPlayedDate
                })
            .ToList();

        var addedEpisodeCandidates = episodeBase
            .Where(e => e.ParentIndexNumber != 0)
            .Where(e => !e.IsVirtualItem)
            .Select(e => new
            {
                e.SeriesPresentationUniqueKey,
                e.ParentIndexNumber,
                EpisodeNumber = e.IndexNumber,
                e.DateCreated
            })
            .ToList();

        var nextEpisodeIds = new HashSet<Guid>();
        var seriesNextIdMap = new Dictionary<string, Guid>();
        var resumableNextIdMap = new Dictionary<string, Guid>();

        foreach (var seriesKey in seriesKeys)
        {
            var candidates = allUnplayedCandidates.Where(c => c.SeriesPresentationUniqueKey == seriesKey);
            var resumable = resumableCandidates.Where(c => c.SeriesPresentationUniqueKey == seriesKey);

            if (positionLookup.TryGetValue(seriesKey, out var pos))
            {
                candidates = candidates.Where(c =>
                    c.ParentIndexNumber > pos.Season
                    || (c.ParentIndexNumber == pos.Season && c.EpisodeNumber > pos.Episode));
                resumable = resumable.Where(c =>
                    c.ParentIndexNumber > pos.Season
                    || (c.ParentIndexNumber == pos.Season && c.EpisodeNumber > pos.Episode));
            }

            var resumableCandidate = resumable
                .OrderByDescending(c => c.ParentIndexNumber)
                .ThenByDescending(c => c.EpisodeNumber)
                .ThenByDescending(c => c.LastPlayedDate)
                .FirstOrDefault();

            var nextCandidate = candidates
                .OrderBy(c => c.ParentIndexNumber)
                .ThenBy(c => c.EpisodeNumber)
                .FirstOrDefault();

            if (nextCandidate is not null && nextCandidate.Id != Guid.Empty)
            {
                nextEpisodeIds.Add(nextCandidate.Id);
                seriesNextIdMap[seriesKey] = nextCandidate.Id;
            }

            if (resumableCandidate is not null && resumableCandidate.Id != Guid.Empty)
            {
                nextEpisodeIds.Add(resumableCandidate.Id);
                resumableNextIdMap[seriesKey] = resumableCandidate.Id;
            }
        }

        var seriesNextPlayedIdMap = new Dictionary<string, Guid>();
        if (includeWatchedForRewatching)
        {
            var allPlayedBase = episodeBase
                .Where(e => e.ParentIndexNumber != 0)
                .Where(e => !e.IsVirtualItem)
                .Where(e => playedEpisodeIds.Contains(e.Id));
            var allPlayedCandidates = allPlayedBase
                .Select(e => new
                {
                    e.Id,
                    e.SeriesPresentationUniqueKey,
                    e.ParentIndexNumber,
                    EpisodeNumber = e.IndexNumber
                })
                .ToList();

            foreach (var seriesKey in seriesKeys)
            {
                if (!lastWatchedByDateInfo.TryGetValue(seriesKey, out var lastByDateId))
                {
                    continue;
                }

                var lastByDateEntity = lastWatchedEpisodes.GetValueOrDefault(lastByDateId);
                if (lastByDateEntity is null)
                {
                    continue;
                }

                var playedCandidates = allPlayedCandidates
                    .Where(c => c.SeriesPresentationUniqueKey == seriesKey);

                if (lastByDateEntity.ParentIndexNumber.HasValue && lastByDateEntity.IndexNumber.HasValue)
                {
                    var lastSeason = lastByDateEntity.ParentIndexNumber.Value;
                    var lastEp = lastByDateEntity.IndexNumber.Value;
                    playedCandidates = playedCandidates.Where(c =>
                        c.ParentIndexNumber > lastSeason
                        || (c.ParentIndexNumber == lastSeason && c.EpisodeNumber > lastEp));
                }

                var nextPlayedCandidate = playedCandidates
                    .OrderBy(c => c.ParentIndexNumber)
                    .ThenBy(c => c.EpisodeNumber)
                    .FirstOrDefault();

                if (nextPlayedCandidate is not null && nextPlayedCandidate.Id != Guid.Empty)
                {
                    nextEpisodeIds.Add(nextPlayedCandidate.Id);
                    seriesNextPlayedIdMap[seriesKey] = nextPlayedCandidate.Id;
                }
            }
        }

        var nextEpisodes = new Dictionary<Guid, BaseItemEntity>();
        if (nextEpisodeIds.Count > 0)
        {
            var nextQuery = context.BaseItems.AsNoTracking().Where(e => nextEpisodeIds.Contains(e.Id));
            nextQuery = _queryHelpers.ApplyNavigations(nextQuery, filter).AsSingleQuery();
            nextEpisodes = nextQuery.ToDictionary(e => e.Id);
        }

        var result = new Dictionary<string, NextUpEpisodeBatchResult>();
        foreach (var seriesKey in seriesKeys)
        {
            var batchResult = new NextUpEpisodeBatchResult
            {
                LastPlayedDate = latestPlayedBySeries.GetValueOrDefault(seriesKey)
            };

            if (lastWatchedInfo.TryGetValue(seriesKey, out var lwId) && lwId != Guid.Empty)
            {
                if (lastWatchedEpisodes.TryGetValue(lwId, out var entity))
                {
                    batchResult.LastWatched = _queryHelpers.DeserializeBaseItem(entity, filter.SkipDeserialization);
                    batchResult.LastWatchedCompletedDate = completedLastWatchedDates.GetValueOrDefault(lwId);

                    if (entity.ParentIndexNumber.HasValue && entity.IndexNumber.HasValue)
                    {
                        var addedCandidates = addedEpisodeCandidates
                            .Where(candidate => candidate.SeriesPresentationUniqueKey == seriesKey)
                            .Where(candidate => candidate.ParentIndexNumber > entity.ParentIndexNumber.Value
                                || (candidate.ParentIndexNumber == entity.ParentIndexNumber.Value
                                    && candidate.EpisodeNumber > entity.IndexNumber.Value))
                            .ToList();
                        if (addedCandidates.Count > 0 && addedCandidates.All(candidate => candidate.DateCreated.HasValue && candidate.DateCreated.Value != DateTime.MinValue))
                        {
                            batchResult.FirstNewEpisodeAddedDate = addedCandidates.Min(candidate => candidate.DateCreated);
                        }
                    }
                }
            }

            if (seriesNextIdMap.TryGetValue(seriesKey, out var nextId) && nextEpisodes.TryGetValue(nextId, out var nextEntity))
            {
                batchResult.NextUp = _queryHelpers.DeserializeBaseItem(nextEntity, filter.SkipDeserialization);
            }

            if (resumableNextIdMap.TryGetValue(seriesKey, out var resumableId) && nextEpisodes.TryGetValue(resumableId, out var resumableEntity))
            {
                batchResult.ResumableNext = _queryHelpers.DeserializeBaseItem(resumableEntity, filter.SkipDeserialization);
            }

            if (includeSpecials && specialsBySeriesKey.TryGetValue(seriesKey, out var specials))
            {
                batchResult.Specials = specials.Select(s => _queryHelpers.DeserializeBaseItem(s, filter.SkipDeserialization)!).ToList();
            }
            else
            {
                batchResult.Specials = Array.Empty<BaseItemDto>();
            }

            if (includeWatchedForRewatching)
            {
                if (lastWatchedByDateInfo.TryGetValue(seriesKey, out var lastByDateId) &&
                    lastWatchedEpisodes.TryGetValue(lastByDateId, out var lastByDateEntity))
                {
                    batchResult.LastWatchedForRewatching = _queryHelpers.DeserializeBaseItem(lastByDateEntity, filter.SkipDeserialization);
                }

                if (seriesNextPlayedIdMap.TryGetValue(seriesKey, out var nextPlayedId) &&
                    nextEpisodes.TryGetValue(nextPlayedId, out var nextPlayedEntity))
                {
                    batchResult.NextPlayedForRewatching = _queryHelpers.DeserializeBaseItem(nextPlayedEntity, filter.SkipDeserialization);
                }
            }

            result[seriesKey] = batchResult;
        }

        return result;
    }

    private IQueryable<BaseItemEntity> GetEpisodeVersions(JellyfinDbContext context, InternalItemsQuery filter)
    {
        // Playback belongs to the selected version, which must retain the same access and library scope.
        var includeAlternateVersions = filter.IncludeAlternateVersions;
        var includeOwnedItems = filter.IncludeOwnedItems;
        try
        {
            filter.IncludeAlternateVersions = true;
            filter.IncludeOwnedItems = true;
            return _queryHelpers.TranslateQuery(
                context.BaseItems.AsNoTracking().Where(e => e.Type == _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode]),
                context,
                filter);
        }
        finally
        {
            filter.IncludeAlternateVersions = includeAlternateVersions;
            filter.IncludeOwnedItems = includeOwnedItems;
        }
    }
}
