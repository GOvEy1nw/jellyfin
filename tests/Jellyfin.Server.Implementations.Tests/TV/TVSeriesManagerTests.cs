using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.TV;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

[Collection("TVSeriesManager static state")]
public sealed class TVSeriesManagerTests
{
    [Fact]
    public void NextUp_RanksRecentPartialPlaybackAheadOfAnOlderCompletedEpisode()
    {
        var oldLastWatched = new Episode { Id = Guid.NewGuid() };
        var oldNext = new Episode { Id = Guid.NewGuid() };
        var recentNext = new Episode { Id = Guid.NewGuid() };
        var oldDate = new DateTime(2025, 1, 1);
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["old"] = new() { LastWatched = oldLastWatched, NextUp = oldNext },
            ["recent-partial"] = new() { LastPlayedDate = new DateTime(2025, 1, 2), NextUp = recentNext }
        };

        var nextUp = GetNextUp(["old", "recent-partial"], results, new Dictionary<Guid, UserItemData>
        {
            [oldLastWatched.Id] = CreateUserData(oldLastWatched.Id, lastPlayedDate: oldDate),
            [recentNext.Id] = CreateUserData(recentNext.Id, playbackPositionTicks: 1)
        });

        Assert.Equal([recentNext.Id, oldNext.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_UsesScopedLastPlayedDateForRanking()
    {
        var completed = new Mock<Episode> { CallBase = true };
        completed.Object.Id = Guid.NewGuid();
        completed.Setup(episode => episode.GetMediaSources(false)).Returns(Array.Empty<MediaSourceInfo>());
        var completedAlternate = new Episode { Id = Guid.NewGuid(), PrimaryVersionId = completed.Object.Id };
        var scopedNext = new Episode { Id = Guid.NewGuid() };
        var otherShow = new Episode { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["scoped"] = new() { LastWatched = completed.Object, NextUp = scopedNext },
            ["other"] = new() { LastPlayedDate = new DateTime(2025, 1, 2), NextUp = otherShow }
        };

        var nextUp = GetNextUp(
            ["scoped", "other"],
            results,
            new Dictionary<Guid, UserItemData>
            {
                [completed.Object.Id] = CreateUserData(completed.Object.Id, played: true),
                [completedAlternate.Id] = CreateUserData(completedAlternate.Id, lastPlayedDate: new DateTime(2025, 1, 10)),
                [movie.Id] = CreateUserData(movie.Id, lastPlayedDate: new DateTime(2025, 1, 3), playbackPositionTicks: 1)
            },
            movies: [movie],
            enableMovies: true,
            configureLibraryManager: manager =>
            {
                manager.Setup(library => library.GetLocalAlternateVersionIds(It.Is<Video>(video => video.Id.Equals(completed.Object.Id))))
                    .Returns([completedAlternate.Id]);
                manager.Setup(library => library.GetItemById(completedAlternate.Id)).Returns(completedAlternate);
            });

        Assert.Equal([movie.Id, otherShow.Id, scopedNext.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_PagesSortedFirstPartialEpisodesWithoutCompletedPredecessors()
    {
        var oldest = new Episode { Id = Guid.NewGuid() };
        var middle = new Episode { Id = Guid.NewGuid() };
        var newest = new Episode { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["oldest"] = new() { LastPlayedDate = new DateTime(2025, 1, 1), NextUp = oldest },
            ["newest"] = new() { LastPlayedDate = new DateTime(2025, 1, 3), NextUp = newest },
            ["middle"] = new() { LastPlayedDate = new DateTime(2025, 1, 2), NextUp = middle }
        };

        var nextUp = GetNextUp(
            ["oldest", "newest", "middle"],
            results,
            new Dictionary<Guid, UserItemData>(),
            startIndex: 1,
            limit: 1);

        Assert.Equal([middle.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_KeepsResumableExclusionAndRewatchInterleaving()
    {
        var normal = new Episode { Id = Guid.NewGuid() };
        var resumable = new Episode { Id = Guid.NewGuid() };
        var rewatchAnchor = new Episode { Id = Guid.NewGuid() };
        var rewatch = new Episode { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["normal"] = new() { LastPlayedDate = new DateTime(2025, 1, 3), NextUp = normal },
            ["resumable"] = new() { LastPlayedDate = new DateTime(2025, 1, 2), NextUp = resumable },
            ["rewatch"] = new()
            {
                LastWatchedForRewatching = rewatchAnchor,
                NextPlayedForRewatching = rewatch
            }
        };

        var data = new Dictionary<Guid, UserItemData>
        {
            [rewatchAnchor.Id] = CreateUserData(rewatchAnchor.Id, lastPlayedDate: new DateTime(2025, 1, 1)),
            [resumable.Id] = CreateUserData(resumable.Id, playbackPositionTicks: 1)
        };

        var nextUp = GetNextUp(
            ["normal", "resumable", "rewatch"],
            results,
            data,
            enableRewatching: true,
            enableResumable: false);

        Assert.Equal([normal.Id, rewatch.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_RanksNewContentBoostAheadOfRecentMoviePlayback()
    {
        var completed = new Episode { Id = Guid.NewGuid() };
        var nextEpisode = new Episode { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["series"] = new()
            {
                LastWatched = completed,
                LastPlayedDate = new DateTime(2025, 1, 1),
                LastWatchedCompletedDate = new DateTime(2025, 1, 1),
                FirstNewEpisodeAddedDate = new DateTime(2025, 1, 5),
                NextUp = nextEpisode
            }
        };

        var nextUp = GetNextUp(
            ["series"],
            results,
            new Dictionary<Guid, UserItemData>
            {
                [completed.Id] = CreateUserData(completed.Id, lastPlayedDate: new DateTime(2025, 1, 1), played: true),
                [movie.Id] = CreateUserData(movie.Id, lastPlayedDate: new DateTime(2025, 1, 4), playbackPositionTicks: 1)
            },
            movies: [movie],
            enableMovies: true);

        Assert.Equal([nextEpisode.Id, movie.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_MovieOnlyResultsApplyCutoffPagingAndIdTieBreak()
    {
        var first = new Movie { Id = new Guid("00000000-0000-0000-0000-000000000001") };
        var second = new Movie { Id = new Guid("00000000-0000-0000-0000-000000000002") };
        var stale = new Movie { Id = Guid.NewGuid() };
        var playedAt = new DateTime(2025, 1, 3);

        var nextUp = GetNextUp(
            [],
            new Dictionary<string, NextUpEpisodeBatchResult>(),
            new Dictionary<Guid, UserItemData>
            {
                [first.Id] = CreateUserData(first.Id, lastPlayedDate: playedAt, playbackPositionTicks: 1),
                [second.Id] = CreateUserData(second.Id, lastPlayedDate: playedAt, playbackPositionTicks: 1),
                [stale.Id] = CreateUserData(stale.Id, lastPlayedDate: new DateTime(2025, 1, 1), playbackPositionTicks: 1)
            },
            startIndex: 1,
            limit: 1,
            nextUpDateCutoff: new DateTime(2025, 1, 2),
            movies: [first, second, stale],
            enableMovies: true);

        Assert.Equal([first.Id], nextUp.Items.Select(item => item.Id));
        Assert.Equal(2, nextUp.TotalRecordCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NextUp_MoviesRequireBothFeatureFlags(bool enableMovies, bool enableResumable)
    {
        var movie = new Movie { Id = Guid.NewGuid() };

        var nextUp = GetNextUp(
            [],
            new Dictionary<string, NextUpEpisodeBatchResult>(),
            new Dictionary<Guid, UserItemData> { [movie.Id] = CreateUserData(movie.Id, playbackPositionTicks: 1) },
            movies: [movie],
            enableMovies: enableMovies,
            enableResumable: enableResumable);

        Assert.Empty(nextUp.Items);
        Assert.Equal(0, nextUp.TotalRecordCount);
    }

    [Fact]
    public void NextUp_SeriesIdUsesScopedEpisodeKeyAndExcludesMovies()
    {
        var requestedSeries = new Series { Id = Guid.NewGuid(), PresentationUniqueKey = "requested" };
        var requestedEpisode = new Episode { Id = Guid.NewGuid() };
        var otherEpisode = new Episode { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["requested"] = new() { NextUp = requestedEpisode },
            ["other"] = new() { NextUp = otherEpisode }
        };

        var nextUp = GetNextUp(
            ["requested", "other"],
            results,
            new Dictionary<Guid, UserItemData> { [movie.Id] = CreateUserData(movie.Id, playbackPositionTicks: 1) },
            movies: [movie],
            enableMovies: true,
            requestedSeries: requestedSeries);

        Assert.Equal([requestedEpisode.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_UsesForwardResumeOnlyWhenEnabled()
    {
        var completed = new Episode { Id = Guid.NewGuid() };
        var normal = new Episode { Id = Guid.NewGuid() };
        var resumable = new Episode { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["series"] = new() { LastWatched = completed, NextUp = normal, ResumableNext = resumable }
        };
        var userData = new Dictionary<Guid, UserItemData>
        {
            [resumable.Id] = CreateUserData(resumable.Id, playbackPositionTicks: 1)
        };

        var withoutResumable = GetNextUp(["series"], results, userData, enableResumable: false);
        var withResumable = GetNextUp(["series"], results, userData, enableResumable: true);

        Assert.Equal([normal.Id], withoutResumable.Items.Select(item => item.Id));
        Assert.Equal([resumable.Id], withResumable.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_PreservesSelectedResumableAlternateVersion()
    {
        var lastWatched = new Mock<Episode> { CallBase = true };
        lastWatched.Object.Id = Guid.NewGuid();
        lastWatched.Object.PrimaryVersionId = Guid.NewGuid();
        lastWatched.Setup(episode => episode.GetMediaSources(false)).Returns(
        [new MediaSourceInfo { Id = lastWatched.Object.Id.ToString("N"), Name = "1080p" }]);

        var resumed = new Mock<Episode> { CallBase = true };
        resumed.Object.Id = Guid.NewGuid();
        var preferredForwardVersion = new Episode { Id = Guid.NewGuid() };
        resumed.Setup(episode => episode.GetMediaSources(false)).Returns(
        [new MediaSourceInfo { Id = preferredForwardVersion.Id.ToString("N"), Name = "1080p" }]);

        var nextUp = GetNextUp(
            ["series"],
            new Dictionary<string, NextUpEpisodeBatchResult>
            {
                ["series"] = new() { LastWatched = lastWatched.Object, ResumableNext = resumed.Object }
            },
            new Dictionary<Guid, UserItemData>
            {
                [lastWatched.Object.Id] = CreateUserData(lastWatched.Object.Id, lastPlayedDate: new DateTime(2025, 1, 1)),
                [resumed.Object.Id] = CreateUserData(resumed.Object.Id, playbackPositionTicks: 1)
            },
            configureLibraryManager: manager => manager
                .Setup(library => library.GetItemById<Episode>(preferredForwardVersion.Id))
                .Returns(preferredForwardVersion));

        Assert.Equal([resumed.Object.Id], nextUp.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_OrdersContentTimelineAndRaisesRecentlyWatchedSeries()
    {
        var tedLastWatched = new Episode { Id = Guid.NewGuid() };
        var ted = new Episode { Id = Guid.NewGuid() };
        var raisingLastWatched = new Episode { Id = Guid.NewGuid() };
        var raising = new Episode { Id = Guid.NewGuid() };
        var raisingAfterWatching = new Episode { Id = Guid.NewGuid() };
        var pittLastWatched = new Episode { Id = Guid.NewGuid() };
        var pitt = new Episode { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var universe = new Episode { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["ted"] = new()
            {
                LastWatched = tedLastWatched,
                LastPlayedDate = new DateTime(2025, 1, 1),
                LastWatchedCompletedDate = new DateTime(2025, 1, 1),
                FirstNewEpisodeAddedDate = new DateTime(2025, 1, 8),
                NextUp = ted
            },
            ["raising"] = new()
            {
                LastWatched = raisingLastWatched,
                LastPlayedDate = new DateTime(2025, 1, 7, 12, 0, 0),
                NextUp = raising
            },
            ["universe"] = new() { LastPlayedDate = new DateTime(2025, 1, 6), ResumableNext = universe },
            ["pitt"] = new()
            {
                LastWatched = pittLastWatched,
                LastPlayedDate = new DateTime(2025, 1, 3),
                LastWatchedCompletedDate = new DateTime(2025, 1, 3),
                FirstNewEpisodeAddedDate = new DateTime(2024, 12, 1),
                NextUp = pitt
            }
        };
        var userData = new Dictionary<Guid, UserItemData>
        {
            [movie.Id] = CreateUserData(movie.Id, lastPlayedDate: new DateTime(2025, 1, 7), playbackPositionTicks: 1)
        };

        var initial = GetNextUp(["ted", "raising", "universe", "pitt"], results, userData, movies: [movie], enableMovies: true);
        results["raising"].LastPlayedDate = new DateTime(2025, 1, 9);
        results["raising"].NextUp = raisingAfterWatching;
        var afterWatchingRaising = GetNextUp(["ted", "raising", "universe", "pitt"], results, userData, movies: [movie], enableMovies: true);

        Assert.Equal([ted.Id, raising.Id, movie.Id, universe.Id, pitt.Id], initial.Items.Select(item => item.Id));
        Assert.Equal([raisingAfterWatching.Id, ted.Id, movie.Id, universe.Id, pitt.Id], afterWatchingRaising.Items.Select(item => item.Id));
    }

    [Fact]
    public void NextUp_CutoffUsesContentBoostOnlyWithCompletedPlayback()
    {
        var dormantLastWatched = new Episode { Id = Guid.NewGuid() };
        var dormant = new Episode { Id = Guid.NewGuid() };
        var missingCompletedDate = new Episode { Id = Guid.NewGuid() };
        var missing = new Episode { Id = Guid.NewGuid() };
        var results = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["dormant"] = new()
            {
                LastWatched = dormantLastWatched,
                LastPlayedDate = new DateTime(2025, 1, 1),
                LastWatchedCompletedDate = new DateTime(2025, 1, 1),
                FirstNewEpisodeAddedDate = new DateTime(2025, 1, 5),
                NextUp = dormant
            },
            ["missing"] = new()
            {
                LastWatched = missingCompletedDate,
                LastPlayedDate = new DateTime(2025, 1, 1),
                FirstNewEpisodeAddedDate = new DateTime(2025, 1, 5),
                NextUp = missing
            }
        };

        var nextUp = GetNextUp(
            ["dormant", "missing"],
            results,
            new Dictionary<Guid, UserItemData>(),
            nextUpDateCutoff: new DateTime(2025, 1, 4));

        Assert.Equal([dormant.Id], nextUp.Items.Select(item => item.Id));
        Assert.Equal(1, nextUp.TotalRecordCount);
    }

    private static QueryResult<BaseItem> GetNextUp(
        IReadOnlyList<string> seriesKeys,
        IReadOnlyDictionary<string, NextUpEpisodeBatchResult> results,
        IReadOnlyDictionary<Guid, UserItemData> userData,
        int? startIndex = null,
        int? limit = null,
        bool enableRewatching = false,
        bool enableResumable = true,
        DateTime? nextUpDateCutoff = null,
        IReadOnlyList<BaseItem>? movies = null,
        bool enableMovies = false,
        Series? requestedSeries = null,
        Action<Mock<ILibraryManager>>? configureLibraryManager = null)
    {
        var user = new User("user", "auth", "reset");
        var userDataManager = new Mock<IUserDataManager>();
        userDataManager.Setup(manager => manager.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns((IReadOnlyList<BaseItem> items, User _) => items
                .Where(item => userData.ContainsKey(item.Id))
                .ToDictionary(item => item.Id, item => userData[item.Id]));
        userDataManager.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem item) => userData.GetValueOrDefault(item.Id));

        var libraryManager = new Mock<ILibraryManager>();
        if (requestedSeries is not null)
        {
            libraryManager.Setup(manager => manager.GetItemById(requestedSeries.Id)).Returns(requestedSeries);
        }

        libraryManager.Setup(manager => manager.GetNextUpSeriesKeys(
            It.IsAny<InternalItemsQuery>(),
            It.IsAny<IReadOnlyCollection<BaseItem>>(),
            It.IsAny<DateTime>()))
            .Returns(seriesKeys);
        libraryManager.Setup(manager => manager.GetNextUpEpisodesBatch(
            It.IsAny<InternalItemsQuery>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .Returns(results);
        libraryManager.Setup(manager => manager.GetItemList(
            It.IsAny<InternalItemsQuery>(),
            It.IsAny<List<BaseItem>>()))
            .Returns(movies ?? []);
        libraryManager.Setup(manager => manager.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns([]);
        libraryManager.Setup(manager => manager.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([]);
        configureLibraryManager?.Invoke(libraryManager);

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(manager => manager.Configuration).Returns(new ServerConfiguration());

        var manager = new TVSeriesManager(userDataManager.Object, libraryManager.Object, configurationManager.Object);
        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = libraryManager.Object;
        try
        {
            return manager.GetNextUp(
                new NextUpQuery
                {
                    User = user,
                    StartIndex = startIndex,
                    Limit = limit,
                    EnableRewatching = enableRewatching,
                    EnableResumable = enableResumable,
                    EnableMovies = enableMovies,
                    SeriesId = requestedSeries?.Id,
                    NextUpDateCutoff = nextUpDateCutoff ?? DateTime.MinValue
                },
                [],
                new DtoOptions());
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }
    }

    private static UserItemData CreateUserData(Guid itemId, DateTime? lastPlayedDate = null, long playbackPositionTicks = 0, bool played = false)
        => new()
        {
            Key = itemId.ToString("N"),
            LastPlayedDate = lastPlayedDate,
            PlaybackPositionTicks = playbackPositionTicks,
            Played = played
        };
}
