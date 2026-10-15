using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Works out where a shared review link should point, and which ids the review may be filed
    /// under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rating is not stored against whatever page the button was pressed on: for a vertical drama
    /// it is stored against the show's <b>season</b> (see RatingsController.ResolveRatingTarget -
    /// each two-minute episode is not a reviewable unit). So the share endpoint must be asked for a
    /// series or an episode page and still find that season's review, which means looking the review
    /// up under a chain of ids rather than under the one id it was handed.
    /// </para>
    /// <para>
    /// The link it hands out is always the <b>season page</b>, so sharing the same review from the
    /// series page, the season page and any episode page gives one address, and that address is the
    /// page where the show's reviews are shown. Resolving only the requested id is what made the
    /// link 404 as soon as you shared from outside the season.
    /// </para>
    /// </remarks>
    public static class ShareResolver
    {
        /// <summary>
        /// The ids to look the review up under, in order, and the id the shared link must open.
        /// </summary>
        /// <param name="itemId">The item the share was asked for - whatever page the button was on.</param>
        /// <param name="ratingTargetId">Where the rating is stored (the season, for a vertical drama).</param>
        /// <param name="seriesId">The show the item belongs to, when there is one.</param>
        /// <returns>
        /// <c>Candidates</c>: the ids to try, in order, without duplicates. <c>Page</c>: the id the
        /// link opens - the rating target when there is one, otherwise the requested item.
        /// </returns>
        public static (IReadOnlyList<Guid> Candidates, Guid Page) Resolve(Guid itemId, Guid? ratingTargetId, Guid? seriesId)
        {
            var candidates = new List<Guid>();
            AddCandidate(candidates, itemId);
            AddCandidate(candidates, ratingTargetId);
            AddCandidate(candidates, seriesId);

            return (candidates, IsSet(ratingTargetId) ? ratingTargetId!.Value : itemId);
        }

        private static void AddCandidate(List<Guid> candidates, Guid? id)
        {
            if (IsSet(id) && !candidates.Contains(id!.Value))
            {
                candidates.Add(id.Value);
            }
        }

        private static bool IsSet(Guid? id) => id.HasValue && id.Value != Guid.Empty;
    }
}
