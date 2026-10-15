using System;
using System.Collections.Generic;
using Emby.Server.Implementations.TV;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.TV;

public sealed class TVSeriesManagerTests : IDisposable
{
    private readonly ILibraryManager? _previousLibraryManager = BaseItem.LibraryManager;

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetNextUp_WithEnableUserDataFalse_StillRequestsUserDataInBatchQuery()
    {
        var userDataManager = new Mock<IUserDataManager>();
        var libraryManager = new Mock<ILibraryManager>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        var user = new User("test", "auth", "reset");
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, PresentationUniqueKey = "test-series-key" };
        libraryManager.Setup(l => l.GetItemById(seriesId)).Returns(series);

        InternalItemsQuery? capturedQuery = null;
        libraryManager.Setup(l => l.GetNextUpEpisodesBatch(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Callback<InternalItemsQuery, IReadOnlyList<string>, bool, bool>((q, _, _, _) => capturedQuery = q)
            .Returns(new Dictionary<string, NextUpEpisodeBatchResult>());

        var manager = new TVSeriesManager(userDataManager.Object, libraryManager.Object, configurationManager.Object);

        var request = new NextUpQuery
        {
            User = user,
            SeriesId = seriesId
        };
        var options = new DtoOptions(false)
        {
            EnableUserData = false
        };

        manager.GetNextUp(request, options);

        Assert.NotNull(capturedQuery);
        Assert.True(capturedQuery.DtoOptions.EnableUserData);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void GetNextUp_WithResumableEpisode_RespectsEnableResumable(bool enableResumable, int expectedCount)
    {
        var userDataManager = new Mock<IUserDataManager>();
        var libraryManager = new Mock<ILibraryManager>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        BaseItem.LibraryManager = libraryManager.Object;
        libraryManager.Setup(l => l.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns([]);
        libraryManager.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([]);

        var user = new User("test", "auth", "reset");
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, PresentationUniqueKey = "test-series-key" };
        libraryManager.Setup(l => l.GetItemById(seriesId)).Returns(series);

        var nextEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var batchResult = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["test-series-key"] = new NextUpEpisodeBatchResult
            {
                NextUp = nextEpisode
            }
        };

        libraryManager.Setup(l => l.GetNextUpEpisodesBatch(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Returns(batchResult);

        userDataManager.Setup(u => u.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns(new Dictionary<Guid, UserItemData>
            {
                [nextEpisode.Id] = new UserItemData { Key = "test-key", PlaybackPositionTicks = 50_000 }
            });

        var manager = new TVSeriesManager(userDataManager.Object, libraryManager.Object, configurationManager.Object);

        var request = new NextUpQuery
        {
            User = user,
            SeriesId = seriesId,
            EnableResumable = enableResumable
        };
        var options = new DtoOptions(false)
        {
            EnableUserData = false
        };

        var result = manager.GetNextUp(request, options);

        Assert.Equal(expectedCount, result.TotalRecordCount);
        if (expectedCount > 0)
        {
            Assert.Equal(nextEpisode.Id, result.Items[0].Id);
        }
    }

    [Fact]
    public void GetNextUp_WithWatchedSpecial_FiltersOutPlayedSpecial()
    {
        var userDataManager = new Mock<IUserDataManager>();
        var libraryManager = new Mock<ILibraryManager>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.Configuration).Returns(new ServerConfiguration
        {
            DisplaySpecialsWithinSeasons = true
        });

        BaseItem.LibraryManager = libraryManager.Object;
        libraryManager.Setup(l => l.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns([]);
        libraryManager.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([]);

        var user = new User("test", "auth", "reset");
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, PresentationUniqueKey = "test-series-key" };
        libraryManager.Setup(l => l.GetItemById(seriesId)).Returns(series);

        var specialEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            ParentIndexNumber = 0,
            IndexNumber = 1,
            AirsBeforeSeasonNumber = 1
        };
        var regularEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            ParentIndexNumber = 1,
            IndexNumber = 1
        };

        var batchResult = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["test-series-key"] = new NextUpEpisodeBatchResult
            {
                NextUp = regularEpisode,
                Specials = [specialEpisode]
            }
        };

        libraryManager.Setup(l => l.GetNextUpEpisodesBatch(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Returns(batchResult);

        libraryManager.Setup(l => l.Sort(It.IsAny<IEnumerable<BaseItem>>(), user, It.IsAny<IEnumerable<(ItemSortBy, SortOrder)>>()))
            .Returns<IEnumerable<BaseItem>, User, IEnumerable<(ItemSortBy, SortOrder)>>((items, _, _) => items);

        userDataManager.Setup(u => u.GetUserData(user, specialEpisode))
            .Returns(new UserItemData { Key = "special-key", Played = true });
        userDataManager.Setup(u => u.GetUserData(user, regularEpisode))
            .Returns(new UserItemData { Key = "regular-key", Played = false });
        userDataManager.Setup(u => u.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns(new Dictionary<Guid, UserItemData>());

        var manager = new TVSeriesManager(userDataManager.Object, libraryManager.Object, configurationManager.Object);

        var request = new NextUpQuery
        {
            User = user,
            SeriesId = seriesId,
            EnableResumable = true
        };
        var options = new DtoOptions(false)
        {
            EnableUserData = false
        };

        var result = manager.GetNextUp(request, options);

        Assert.Equal(1, result.TotalRecordCount);
        Assert.Equal(regularEpisode.Id, result.Items[0].Id);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void GetNextUp_WithResumableAlternateVersion_RespectsEnableResumable(bool enableResumable, int expectedCount)
    {
        var userDataManager = new Mock<IUserDataManager>();
        var libraryManager = new Mock<ILibraryManager>();
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        BaseItem.LibraryManager = libraryManager.Object;

        var user = new User("test", "auth", "reset");
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId, PresentationUniqueKey = "test-series-key" };
        libraryManager.Setup(l => l.GetItemById(seriesId)).Returns(series);

        var primaryEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var alternateEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, PrimaryVersionId = primaryEpisode.Id };

        libraryManager.Setup(l => l.GetLinkedAlternateVersions(primaryEpisode)).Returns([alternateEpisode]);
        libraryManager.Setup(l => l.GetLinkedAlternateVersions(alternateEpisode)).Returns([]);
        libraryManager.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([]);

        var batchResult = new Dictionary<string, NextUpEpisodeBatchResult>
        {
            ["test-series-key"] = new NextUpEpisodeBatchResult
            {
                NextUp = primaryEpisode
            }
        };

        libraryManager.Setup(l => l.GetNextUpEpisodesBatch(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Returns(batchResult);

        // Primary has 0 resume ticks, but alternate version has resume ticks
        userDataManager.Setup(u => u.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user))
            .Returns(new Dictionary<Guid, UserItemData>
            {
                [primaryEpisode.Id] = new UserItemData { Key = "primary-key", PlaybackPositionTicks = 0 },
                [alternateEpisode.Id] = new UserItemData { Key = "alternate-key", PlaybackPositionTicks = 75_000 }
            });

        var manager = new TVSeriesManager(userDataManager.Object, libraryManager.Object, configurationManager.Object);

        var request = new NextUpQuery
        {
            User = user,
            SeriesId = seriesId,
            EnableResumable = enableResumable
        };
        var options = new DtoOptions(false)
        {
            EnableUserData = false
        };

        var result = manager.GetNextUp(request, options);

        Assert.Equal(expectedCount, result.TotalRecordCount);
        if (expectedCount > 0)
        {
            Assert.Equal(primaryEpisode.Id, result.Items[0].Id);
        }
    }
}
