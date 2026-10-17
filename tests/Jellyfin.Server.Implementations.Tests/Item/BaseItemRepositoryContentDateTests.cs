using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class BaseItemRepositoryContentDateTests : SqliteDbTestFixture
{
    private const string SeriesType = "MediaBrowser.Controller.Entities.TV.Series";
    private const string SeasonType = "MediaBrowser.Controller.Entities.TV.Season";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";
    private static readonly DateTime Created = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Guid _library = Guid.NewGuid();

    [Theory]
    [InlineData(SortOrder.Ascending, false)]
    [InlineData(SortOrder.Descending, false)]
    [InlineData(SortOrder.Ascending, true)]
    [InlineData(SortOrder.Descending, true)]
    public void DateLastContentAdded_InterleavesMoviesAndSeries(SortOrder direction, bool search)
    {
        using var context = CreateDbContext();
        var older = AddSeries(context, Created.AddDays(8), false, false);
        var newer = AddSeries(context, Created.AddDays(10), false, false);
        var movie = AddMovie(context, Created.AddDays(9));
        context.SaveChanges();
        var repository = CreateBaseItemRepository(new ItemTypeLookup());
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            TopParentIds = [_library],
            OrderBy = [(ItemSortBy.DateLastContentAdded, direction)],
            SearchTerm = search ? "probe" : null
        };

        Guid[] expected = direction == SortOrder.Descending
            ? [newer.SeriesId, movie.Id, older.SeriesId]
            : [older.SeriesId, movie.Id, newer.SeriesId];
        Assert.Equal(expected, repository.GetItemList(query).Select(item => item.Id));

        query.StartIndex = 1;
        query.Limit = 1;
        var page = repository.GetItems(query);
        Assert.Equal(3, page.TotalRecordCount);
        Assert.Equal(movie.Id, Assert.Single(page.Items).Id);

        query.StartIndex = 0;
        query.Limit = null;
        query.OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)];
        Assert.Equal(movie.Id, repository.GetItemList(query).First().Id);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void LatestMixed_UsesEpisodeDatesWithoutChangingRepresentativeMetadata(bool bulk, bool multipleSeasons, bool restrictedContainer)
    {
        using var context = CreateDbContext();
        var newer = AddSeries(context, Created.AddDays(10), bulk, multipleSeasons);
        var older = AddSeries(context, Created.AddDays(8), bulk, multipleSeasons);
        var movie = AddMovie(context, Created.AddDays(9));
        var excludedMovie = AddMovie(context, Created.AddDays(20));
        excludedMovie.TopParentId = Guid.NewGuid();
        var blockedMovie = AddMovie(context, Created.AddDays(25));
        blockedMovie.InheritedParentalRatingValue = 100;
        var excludedEpisode = CreateItem(EpisodeType, Created.AddDays(30));
        excludedEpisode.SeriesId = newer.SeriesId;
        excludedEpisode.SeriesName = "probe shared name";
        excludedEpisode.TopParentId = excludedMovie.TopParentId;
        context.BaseItems.Add(excludedEpisode);
        context.SaveChanges();
        if (restrictedContainer)
        {
            var containerId = multipleSeasons ? newer.SeasonId : newer.SeriesId;
            context.BaseItems.Single(item => item.Id.Equals(containerId)).InheritedParentalRatingValue = 100;
            context.SaveChanges();
        }

        var repository = CreateBaseItemRepository(new ItemTypeLookup());
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            TopParentIds = [_library],
            MaxParentalRating = new ParentalRatingScore(50, null),
            IsVirtualItem = false,
            Limit = 3
        };
        var newerId = bulk && !restrictedContainer ? multipleSeasons ? newer.SeasonId : newer.SeriesId : newer.EpisodeId;
        var olderId = bulk ? multipleSeasons ? older.SeasonId : older.SeriesId : older.EpisodeId;

        var items = repository.GetLatestItemList(query, CollectionType.unknown);
        Assert.Equal([newerId, movie.Id, olderId], items.Select(item => item.Id));
        Assert.Equal(bulk && !restrictedContainer ? Created : Created.AddDays(10), items[0].DateCreated);

        query.Limit = 2;
        Assert.Equal([newerId, movie.Id], repository.GetLatestItemList(query, CollectionType.unknown).Select(item => item.Id));

        query.IncludeItemTypes = [BaseItemKind.Episode];
        Assert.Equal([newerId, olderId], repository.GetLatestItemList(query, CollectionType.tvshows).Select(item => item.Id));
    }

    private (Guid SeriesId, Guid SeasonId, Guid EpisodeId) AddSeries(JellyfinDbContext context, DateTime contentDate, bool bulk, bool multipleSeasons)
    {
        var series = CreateItem(SeriesType, Created);
        series.IsFolder = true;
        series.DateLastMediaAdded = contentDate;
        var season = CreateItem(SeasonType, Created);
        season.IsFolder = true;
        season.SeriesId = series.Id;
        var newest = CreateItem(EpisodeType, contentDate);
        newest.SeriesId = series.Id;
        newest.SeasonId = season.Id;
        newest.SeriesName = "probe shared name";
        var other = CreateItem(EpisodeType, bulk ? contentDate.AddMinutes(-1) : Created);
        other.SeriesId = series.Id;
        other.SeasonId = season.Id;
        other.SeriesName = newest.SeriesName;
        context.BaseItems.AddRange(series, season, newest, other);
        if (multipleSeasons)
        {
            var secondSeason = CreateItem(SeasonType, Created);
            secondSeason.IsFolder = true;
            secondSeason.SeriesId = series.Id;
            context.BaseItems.Add(secondSeason);
        }

        return (series.Id, season.Id, newest.Id);
    }

    private BaseItemEntity AddMovie(JellyfinDbContext context, DateTime date)
    {
        var movie = CreateItem(MovieType, date);
        context.BaseItems.Add(movie);
        return movie;
    }

    private BaseItemEntity CreateItem(string type, DateTime date)
    {
        var id = Guid.NewGuid();
        return new BaseItemEntity
        {
            Id = id,
            Type = type,
            Name = "probe shared name",
            CleanName = "probe shared name",
            SortName = "probe shared name",
            PresentationUniqueKey = id.ToString("N"),
            MediaType = "Video",
            DateCreated = date,
            InheritedParentalRatingValue = 0,
            TopParentId = _library
        };
    }
}
