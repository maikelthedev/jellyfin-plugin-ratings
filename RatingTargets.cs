using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// The rule for what a rating is stored against, shared by every endpoint that has to find one.
    /// </summary>
    /// <remarks>
    /// It lives here rather than inside one controller because the share link has to agree with the
    /// write path exactly: the rating goes on the season, so the share link has to look on the
    /// season too, or sharing from the series page or an episode page asks for a review that was
    /// never filed under that id.
    /// </remarks>
    public static class RatingTargets
    {
        /// <summary>
        /// The item a rating is stored against. A vertical drama is reviewed as a whole, so an
        /// episode's rating belongs to its season - reviewing each two-minute episode separately is
        /// meaningless. Falls back to the episode when it has no season.
        /// </summary>
        /// <param name="libraryManager">Library manager, used to walk up to the season and series.</param>
        /// <param name="item">The item the client addressed.</param>
        /// <returns>The item to store against, plus the title and media type to remember for it.</returns>
        public static (BaseItem Item, string Title, string MediaType) Resolve(ILibraryManager libraryManager, BaseItem item)
        {
            if (item is Episode episode)
            {
                var season = episode.SeasonId != Guid.Empty ? libraryManager.GetItemById(episode.SeasonId) : null;

                // Only a vertical drama is reviewed as a whole; a landscape episode is its own unit.
                if (season != null && IsVertical(libraryManager, episode))
                {
                    var series = episode.SeriesId != Guid.Empty ? libraryManager.GetItemById(episode.SeriesId) : null;
                    return (season, series?.Name ?? episode.SeriesName ?? item.Name, "Series");
                }

                return (item, item.Name, "Series");
            }

            // A single-season vertical show (every vertical drama) is reviewed on its season, which
            // is what the season and episode pages both show. A multi-season or landscape show keeps
            // its own item.
            if (item is Series seriesItem && IsVertical(libraryManager, seriesItem))
            {
                var seasons = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = seriesItem.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true
                });
                if (seasons.Count == 1)
                {
                    return (seasons[0], seriesItem.Name, "Series");
                }
            }

            return (item, item.Name, item is Series ? "Series" : "Movie");
        }

        /// <summary>
        /// Whether an item is a vertical (9:16) drama, decided from its video stream rather than from
        /// its library, since the same library holds both shapes.
        /// </summary>
        /// <param name="libraryManager">Library manager, used to look at a show's episodes.</param>
        /// <param name="item">The item to measure.</param>
        /// <returns>True when the picture is taller than it is wide.</returns>
        public static bool IsVertical(ILibraryManager libraryManager, BaseItem item)
        {
            var probe = item;
            if (item is Series || item is Season)
            {
                var episodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = item.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                });
                probe = episodes.OrderBy(e => e.IndexNumber ?? int.MaxValue).FirstOrDefault() ?? item;
            }

            var video = probe.GetMediaStreams()?.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
            return video?.Width.HasValue == true
                && video.Height.HasValue
                && video.Height.Value > video.Width.Value;
        }

        /// <summary>
        /// The show an item belongs to, for the review-lookup chain: a season's and an episode's
        /// series, or a series itself. Null for anything else.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The series id, or null.</returns>
        public static Guid? SeriesIdOf(BaseItem? item) => item switch
        {
            Season season when season.SeriesId != Guid.Empty => season.SeriesId,
            Episode episode when episode.SeriesId != Guid.Empty => episode.SeriesId,
            Series series => series.Id,
            _ => null
        };
    }
}
