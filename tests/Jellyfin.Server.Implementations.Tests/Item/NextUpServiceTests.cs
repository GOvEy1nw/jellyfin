using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;
using LinkedChildType = Jellyfin.Database.Implementations.Entities.LinkedChildType;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers Next Up over episodes with alternate versions: the episode that was watched is the one
/// whose alternate carries the played row, so an episode already seen must not be offered again.
/// </summary>
public sealed class NextUpServiceTests : SqliteDbTestFixture
{
    private const string SeriesKey = "next-up-series";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";

    private readonly NextUpService _service;
    private readonly User _user = new("test", "auth-provider", "reset-provider");

    private readonly Guid _playedViaAlternate = Guid.NewGuid();
    private readonly Guid _unplayed = Guid.NewGuid();
    private readonly Guid _libraryA = Guid.NewGuid();
    private readonly Guid _libraryB = Guid.NewGuid();
    private readonly Guid _resumableAlternate = Guid.NewGuid();

    public NextUpServiceTests()
    {
        var itemTypeLookup = new ItemTypeLookup();

        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _service = new NextUpService(
            CreateDbContextFactory(),
            itemTypeLookup,
            CreateBaseItemRepository(itemTypeLookup));
    }

    [Fact]
    public void GetNextUpEpisodesBatch_EpisodePlayedThroughItsAlternateVersion_OffersTheOneAfterIt()
    {
        var batch = _service.GetNextUpEpisodesBatch(
            new InternalItemsQuery(_user),
            [SeriesKey],
            includeSpecials: false,
            includeWatchedForRewatching: false)[SeriesKey];

        Assert.Equal(_playedViaAlternate, batch.LastWatched?.Id);
        Assert.Equal(_unplayed, batch.NextUp?.Id);
    }

    [Fact]
    public void GetNextUpEpisodesBatch_UsesTheScopedAlternateResumptionWithoutLeakingAnotherLibrary()
    {
        var batch = _service.GetNextUpEpisodesBatch(
            new InternalItemsQuery(_user) { TopParentIds = [_libraryA] },
            [SeriesKey],
            includeSpecials: false,
            includeWatchedForRewatching: false)[SeriesKey];

        Assert.Equal(_unplayed, batch.NextUp?.Id);
        Assert.Equal(_resumableAlternate, batch.ResumableNext?.Id);
        Assert.Equal(new DateTime(2025, 1, 2), batch.LastPlayedDate);
    }

    [Fact]
    public void GetNextUpSeriesKeys_UsesAlternateOnlyProgressBeforeTheCutoff()
    {
        var query = new InternalItemsQuery(_user) { TopParentIds = [_libraryA] };

        var keys = _service.GetNextUpSeriesKeys(query, new DateTime(2025, 2, 1));

        Assert.Equal([SeriesKey], keys);
        Assert.False(query.IncludeAlternateVersions);
        Assert.False(query.IncludeOwnedItems);
    }

    [Fact]
    public void GetNextUpEpisodesBatch_ExcludesRestrictedVersionProgressAndOtherUsers()
    {
        using (var context = CreateDbContext())
        {
            context.BaseItems.Single(item => item.Id.Equals(_resumableAlternate)).InheritedParentalRatingValue = 100;
            var otherUser = new User("other", "auth-provider", "reset-provider");
            context.Users.Add(otherUser);
            context.UserData.Add(new UserData
            {
                ItemId = _unplayed,
                UserId = otherUser.Id,
                CustomDataKey = _unplayed.ToString("N"),
                Played = true,
                LastPlayedDate = new DateTime(2025, 3, 1),
                Item = null!,
                User = null!
            });
            context.SaveChanges();
        }

        var batch = _service.GetNextUpEpisodesBatch(
            new InternalItemsQuery(_user)
            {
                TopParentIds = [_libraryA],
                MaxParentalRating = new ParentalRatingScore(50, null)
            },
            [SeriesKey],
            includeSpecials: false,
            includeWatchedForRewatching: false)[SeriesKey];

        Assert.Equal(_unplayed, batch.NextUp?.Id);
        Assert.Null(batch.ResumableNext);
        Assert.Null(batch.LastPlayedDate);
    }

    [Fact]
    public void GetNextUpEpisodesBatch_KeepsFirstArrivalUntilProgressAndRejectsMissingDates()
    {
        using (var context = CreateDbContext())
        {
            context.UserData.Single(data => data.Played).LastPlayedDate = new DateTime(2025, 1, 1);
            foreach (var episode in context.BaseItems.Where(item => item.TopParentId.Equals(_libraryA)))
            {
                episode.DateCreated = new DateTime(2025, 1, episode.IndexNumber!.Value + 2);
            }

            context.SaveChanges();
        }

        var query = new InternalItemsQuery(_user) { TopParentIds = [_libraryA] };
        var initial = _service.GetNextUpEpisodesBatch(query, [SeriesKey], false, false)[SeriesKey];
        Assert.Equal(new DateTime(2025, 1, 1), initial.LastWatchedCompletedDate);
        Assert.Equal(new DateTime(2025, 1, 4), initial.FirstNewEpisodeAddedDate);

        using (var context = CreateDbContext())
        {
            var newEpisode = Guid.NewGuid();
            AddEpisode(context, newEpisode, 4);
            context.BaseItems.Local.Single(item => item.Id.Equals(newEpisode)).DateCreated = new DateTime(2025, 1, 8);
            context.SaveChanges();
        }

        var laterArrival = _service.GetNextUpEpisodesBatch(query, [SeriesKey], false, false)[SeriesKey];
        Assert.Equal(initial.FirstNewEpisodeAddedDate, laterArrival.FirstNewEpisodeAddedDate);

        using (var context = CreateDbContext())
        {
            context.BaseItems.Single(item => item.Id.Equals(_unplayed)).DateCreated = null;
            context.SaveChanges();
        }

        Assert.Null(_service.GetNextUpEpisodesBatch(query, [SeriesKey], false, false)[SeriesKey].FirstNewEpisodeAddedDate);
    }

    [Fact]
    public void GetNextUpEpisodesBatch_SkipsOldPartialProgressButRetainsItsActivity()
    {
        var nextEpisode = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            var completedId = context.BaseItems.Single(item => item.IndexNumber == 3 && !item.PrimaryVersionId.HasValue).Id;
            context.UserData.AddRange(
                new UserData
                {
                    ItemId = completedId,
                    UserId = _user.Id,
                    CustomDataKey = completedId.ToString("N"),
                    Played = true,
                    LastPlayedDate = new DateTime(2025, 1, 7),
                    Item = null!,
                    User = null!
                },
                new UserData
                {
                    ItemId = _unplayed,
                    UserId = _user.Id,
                    CustomDataKey = _unplayed.ToString("N"),
                    PlaybackPositionTicks = 1,
                    LastPlayedDate = new DateTime(2025, 1, 10),
                    Item = null!,
                    User = null!
                });
            AddEpisode(context, nextEpisode, 5);
            context.SaveChanges();
        }

        var batch = _service.GetNextUpEpisodesBatch(
            new InternalItemsQuery(_user) { TopParentIds = [_libraryA] },
            [SeriesKey],
            false,
            false)[SeriesKey];

        Assert.Equal(nextEpisode, batch.NextUp?.Id);
        Assert.Null(batch.ResumableNext);
        Assert.Equal(new DateTime(2025, 1, 10), batch.LastPlayedDate);
    }

    private void Seed(JellyfinDbContext context)
    {
        context.Users.Add(_user);

        AddEpisode(context, _playedViaAlternate, 1);
        AddEpisode(context, _unplayed, 2);
        var resumablePrimary = Guid.NewGuid();
        AddEpisode(context, resumablePrimary, 3);
        var otherLibrary = Guid.NewGuid();
        AddEpisode(context, otherLibrary, 4, _libraryB);

        // The second file of the first episode, and the only row the playback was recorded against.
        // It presents under its primary's key, which is what keeps it out of the candidate list.
        var alternateId = Guid.NewGuid();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = alternateId,
            Type = EpisodeType,
            Name = "Episode 1 4K",
            SeriesPresentationUniqueKey = SeriesKey,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            PresentationUniqueKey = _playedViaAlternate.ToString("N"),
            PrimaryVersionId = _playedViaAlternate,
            OwnerId = _playedViaAlternate,
            TopParentId = _libraryA,
            InheritedParentalRatingValue = 0
        });
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _resumableAlternate,
            Type = EpisodeType,
            Name = "Episode 3 4K",
            SeriesPresentationUniqueKey = SeriesKey,
            ParentIndexNumber = 1,
            IndexNumber = 3,
            PresentationUniqueKey = resumablePrimary.ToString("N"),
            PrimaryVersionId = resumablePrimary,
            OwnerId = resumablePrimary,
            TopParentId = _libraryA,
            InheritedParentalRatingValue = 0
        });

        context.SaveChanges();

        // The link the scanner writes alongside PrimaryVersionId, and the hop the played state
        // reaches the alternate through.
        context.LinkedChildren.Add(new LinkedChildEntity
        {
            ParentId = _playedViaAlternate,
            ChildId = alternateId,
            ChildType = LinkedChildType.LocalAlternateVersion,
            SortOrder = 0
        });

        context.UserData.Add(new UserData
        {
            ItemId = alternateId,
            UserId = _user.Id,
            CustomDataKey = alternateId.ToString("N"),
            Played = true,
            Item = null!,
            User = null!
        });
        context.UserData.Add(new UserData
        {
            ItemId = _resumableAlternate,
            UserId = _user.Id,
            CustomDataKey = _resumableAlternate.ToString("N"),
            LastPlayedDate = new DateTime(2025, 1, 2),
            PlaybackPositionTicks = 1,
            Item = null!,
            User = null!
        });
        context.UserData.Add(new UserData
        {
            ItemId = otherLibrary,
            UserId = _user.Id,
            CustomDataKey = otherLibrary.ToString("N"),
            LastPlayedDate = new DateTime(2025, 1, 3),
            PlaybackPositionTicks = 1,
            Item = null!,
            User = null!
        });

        context.SaveChanges();
    }

    private void AddEpisode(JellyfinDbContext context, Guid id, int indexNumber, Guid? topParentId = null)
        => context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = EpisodeType,
            Name = $"Episode {indexNumber}",
            SeriesPresentationUniqueKey = SeriesKey,
            ParentIndexNumber = 1,
            IndexNumber = indexNumber,
            PresentationUniqueKey = id.ToString("N"),
            TopParentId = topParentId ?? _libraryA,
            InheritedParentalRatingValue = 0
        });
}
