using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Scheduled task that mirrors the plugin's ratings and reviews into each item's NFO as an
    /// inert <c>&lt;pluginratings&gt;</c> element, and (optionally) restores records from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plugin store stays authoritative; the NFO is a backup that travels with the media and
    /// survives losing <c>ratings.json</c>. Jellyfin ignores the element (it skips NFO elements it
    /// does not know) and never rewrites the file (no metadata saver is enabled in these
    /// libraries), so nothing else can clobber it.
    /// </para>
    /// <para>
    /// Writes are skipped when the payload has not changed, so a normal run touches no files and
    /// no mtimes: that matters because a changed NFO is what nudges Jellyfin into a refresh.
    /// </para>
    /// <para>
    /// Restore is off by default: if the NFO were read back unconditionally, deleting a rating in
    /// the UI would resurrect it on the next run. With <c>ImportRatingsFromNfo</c> enabled the
    /// task only imports records the store does not already have, which makes it a recovery tool
    /// rather than a second source of truth.
    /// </para>
    /// </remarks>
    public class NfoMirrorService : IScheduledTask
    {
        private readonly ILogger<NfoMirrorService> _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly ILibraryManager _libraryManager;
        private readonly RatingsRepository _repository;

        /// <summary>
        /// Initializes a new instance of the <see cref="NfoMirrorService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="appPaths">Application paths (for the backup directory).</param>
        /// <param name="libraryManager">Library manager, used to resolve a rating's item and path.</param>
        /// <param name="repository">The plugin's ratings store.</param>
        public NfoMirrorService(
            ILogger<NfoMirrorService> logger,
            IApplicationPaths appPaths,
            ILibraryManager libraryManager,
            RatingsRepository repository)
        {
            _logger = logger;
            _appPaths = appPaths;
            _libraryManager = libraryManager;
            _repository = repository;
        }

        /// <inheritdoc />
        public string Name => "Mirror ratings and reviews into NFOs";

        /// <inheritdoc />
        public string Key => "K3ntasNfoMirror";

        /// <inheritdoc />
        public string Description =>
            "Writes every rating and review into its item's .nfo as an inert <pluginratings> element, "
            + "so the plugin's data is backed up next to the media. Optionally restores records the "
            + "plugin no longer has (ImportRatingsFromNfo).";

        /// <inheritdoc />
        public string Category => "K3ntas Ratings";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Task.Run(
                () =>
                {
                    try
                    {
                        Run(progress, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "NFO mirror failed");
                    }
                },
                cancellationToken);
        }

        private void Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.MirrorRatingsToNfo)
            {
                _logger.LogInformation("NFO mirror disabled (MirrorRatingsToNfo is off) — nothing to do");
                return;
            }

            var all = _repository.GetAllRatings() ?? new List<Models.UserRating>();
            var groups = all.GroupBy(r => r.ItemId).ToList();

            // Group by the NFO the ratings belong to, NOT by item: a series' tvshow.nfo carries the
            // series' own ratings AND its seasons' (a season has no NFO of its own, so both map to the
            // series file), and the block is a single element per file.
            var byNfo = new Dictionary<string, List<Models.UserRating>>(StringComparer.Ordinal);
            var noItem = 0;
            var noNfo = 0;
            foreach (var group in groups)
            {
                var item = _libraryManager.GetItemById(group.Key);
                if (item is null)
                {
                    noItem++;
                    continue;
                }

                var nfoPath = NfoPathFor(item);
                if (nfoPath is null || !File.Exists(nfoPath))
                {
                    // No NFO to carry the block: a missing NFO is Jellyfin's own business, and creating
                    // one here would mean inventing metadata. Counted and reported instead.
                    noNfo++;
                    continue;
                }

                if (!byNfo.TryGetValue(nfoPath, out var list))
                {
                    byNfo[nfoPath] = list = new List<Models.UserRating>();
                }

                list.AddRange(group);
            }

            var backups = Path.Combine(_appPaths.DataPath, "ratings", "nfo-backups",
                DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var written = 0;
            var unchanged = 0;
            var failed = 0;
            var restored = 0;
            var done = 0;

            foreach (var (nfoPath, ratings) in byNfo)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(done * 100.0 / Math.Max(byNfo.Count, 1));

                try
                {
                    var existing = File.ReadAllText(nfoPath);
                    var block = NfoMirror.BuildBlock("k3ntas", DateTime.UtcNow, ratings);
                    var merged = NfoMirror.Merge(existing, block);
                    if (string.Equals(merged, existing, StringComparison.Ordinal))
                    {
                        unchanged++;
                    }
                    else
                    {
                        BackupOnce(nfoPath, backups);
                        var tmp = nfoPath + ".tmp";
                        File.WriteAllText(tmp, merged);
                        File.Move(tmp, nfoPath, true);
                        written++;
                    }

                    if (config.ImportRatingsFromNfo)
                    {
                        restored += RestoreMissing(NfoMirror.Parse(existing));
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "NFO mirror: could not write {Path}", nfoPath);
                }
            }

            _logger.LogInformation(
                "NFO mirror: {Ratings} ratings on {Groups} items -> {Files} NFOs — {Written} written, "
                + "{Unchanged} unchanged, {NoNfo} without an NFO, {NoItem} not in the library, "
                + "{Failed} failed, {Restored} restored",
                all.Count, groups.Count, byNfo.Count, written, unchanged, noNfo, noItem, failed, restored);
            progress?.Report(100);
        }

        /// <summary>
        /// Imports ratings that only exist in an NFO. Never overwrites what the store already has,
        /// so a deleted rating cannot come back.
        /// </summary>
        /// <param name="mirrored">Records read out of the NFO's block.</param>
        /// <returns>How many were restored.</returns>
        private int RestoreMissing(IReadOnlyCollection<MirroredRating> mirrored)
        {
            var restored = 0;
            foreach (var r in mirrored)
            {
                if (_repository.GetUserRating(r.UserId, r.ItemId) != null)
                {
                    continue;
                }

                try
                {
                    _repository.SetRatingAsync(r.UserId, r.ItemId, r.Rating, reviewText: r.ReviewText)
                        .GetAwaiter().GetResult();
                    restored++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NFO mirror: could not restore a rating for {ItemId}", r.ItemId);
                }
            }

            return restored;
        }

        /// <summary>
        /// The NFO that belongs to an item: the series' <c>tvshow.nfo</c> for a series or one of its
        /// seasons (a season folder has no NFO of its own), otherwise the media filename with an .nfo
        /// extension.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The path, or null when the item has no usable path.</returns>
        private static string? NfoPathFor(BaseItem item)
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (item is Season)
            {
                var parent = Directory.GetParent(path!.TrimEnd('/'));
                return parent is null ? null : Path.Combine(parent.FullName, "tvshow.nfo");
            }

            return item is Series
                ? Path.Combine(path!, "tvshow.nfo")
                : Path.ChangeExtension(path!, ".nfo");
        }

        /// <summary>
        /// Keeps the first version of a file that this run is about to change, once per day.
        /// </summary>
        /// <param name="nfoPath">The file being changed.</param>
        /// <param name="backups">Backup directory.</param>
        private static void BackupOnce(string nfoPath, string backups)
        {
            var dst = Path.Combine(backups, Path.GetFileName(nfoPath));
            if (File.Exists(dst))
            {
                return;
            }

            Directory.CreateDirectory(backups);
            File.Copy(nfoPath, dst);
        }
    }
}
