using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Scheduled task that puts back the members a collection has lost, using the backup written by
    /// <see cref="CollectionBackupService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Members are resolved <b>by name</b>, never by the id (which is what changed) or the path
    /// (which is what moved). <see cref="CollectionBackup.NameKey"/> folds the spellings this
    /// library contains together - apostrophes, typographic dashes, diacritics, case - so
    /// "Good Boys Don't Sleep with Vampires" still finds "GOOD BOYS DON_T SLEEP WITH VAMPIRES".
    /// </para>
    /// <para>
    /// It only ever ADDS, and only what the collection does not already have, so running it twice
    /// changes nothing. That also means it cannot tell a member that was lost from one that was
    /// deliberately removed in the last day: if you remove a title from a collection and this task
    /// is enabled, the next run puts it back (the previous night's backup still lists it). It is off
    /// by default for exactly that reason - turn it on as a safety net, or run it on demand from
    /// Scheduled Tasks after fixing a move.
    /// </para>
    /// <para>
    /// It runs an hour BEFORE the backup task, so a collection that lost a member during the day is
    /// compared against last night's file - the one that still lists it. If the backup ran first,
    /// the loss would be baked into the file before anything could notice.
    /// </para>
    /// </remarks>
    public class CollectionRestoreService : IScheduledTask
    {
        private readonly ILogger<CollectionRestoreService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionRestoreService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager, used to resolve names to items.</param>
        /// <param name="collectionManager">Collection manager, used to create collections and add members.</param>
        public CollectionRestoreService(
            ILogger<CollectionRestoreService> logger,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
        }

        /// <inheritdoc />
        public string Name => "Restore collections from the backup";

        /// <inheritdoc />
        public string Key => "K3ntasCollectionRestore";

        /// <inheritdoc />
        public string Description =>
            "Adds back the members a collection has lost, resolving them BY NAME (ids change when Jellyfin "
            + "re-imports a title, and paths change when it moves). Only ever adds, never removes. Off by "
            + "default: with it on, a title you removed from a collection comes back on the next run.";

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
                        _logger.LogError(ex, "Collection restore failed");
                    }
                },
                cancellationToken);
        }

        private void Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.RestoreCollections)
            {
                _logger.LogInformation("Collection restore disabled (RestoreCollections is off) — nothing to do");
                return;
            }

            var dir = string.IsNullOrWhiteSpace(config.BackupCollectionsTo)
                ? "/mnt/sata3/jellyfin/collections"
                : config.BackupCollectionsTo!.Trim();
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Collection restore: no backup folder at {Dir}", dir);
                return;
            }

            var files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                _logger.LogInformation("Collection restore: no backups in {Dir} yet", dir);
                return;
            }

            // What the library has today, indexed the same way the members are keyed.
            var resolvable = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Season },
                Recursive = true
            }))
            {
                var key = KeyFor(item);
                if (!string.IsNullOrEmpty(key) && !resolvable.ContainsKey(key!))
                {
                    resolvable[key!] = item;
                }
            }

            var collections = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OfType<BoxSet>().ToList();

            var added = 0;
            var created = 0;
            var unresolvedTotal = 0;
            var scanned = 0;
            var done = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(done * 100.0 / files.Count);

                var backup = CollectionBackup.Parse(File.ReadAllText(file));
                if (backup is null)
                {
                    _logger.LogWarning("Collection restore: {File} is not a collection backup", Path.GetFileName(file));
                    continue;
                }

                scanned++;
                var nameKey = CollectionBackup.NameKey(backup.Name);
                var box = collections.FirstOrDefault(b => CollectionBackup.NameKey(b.Name) == nameKey);
                if (box is null)
                {
                    try
                    {
                        box = _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = backup.Name
                        }).GetAwaiter().GetResult() as BoxSet;
                        if (box is not null)
                        {
                            collections.Add(box);
                            created++;
                            _logger.LogInformation("Collection restore: recreated \"{Name}\"", backup.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Collection restore: could not recreate \"{Name}\"", backup.Name);
                        continue;
                    }
                }

                if (box is null)
                {
                    continue;
                }

                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var link in box.LinkedChildren ?? Array.Empty<LinkedChild>())
                {
                    var item = link.ItemId.HasValue ? _libraryManager.GetItemById(link.ItemId.Value) : null;
                    var key = item is null ? null : KeyFor(item);
                    if (!string.IsNullOrEmpty(key))
                    {
                        present.Add(key!);
                    }
                }

                // The ids this backup recorded that are no longer items: what a move or a re-import
                // leaves behind, and the signal that a missing member was lost rather than removed.
                var deadIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in backup.Members)
                {
                    if (!string.IsNullOrEmpty(m.Id) && Guid.TryParse(m.Id, out var recorded)
                        && _libraryManager.GetItemById(recorded) is null)
                    {
                        deadIds.Add(m.Id!);
                    }
                }

                var (toAdd, unresolved, leftAlone) = CollectionBackup.Plan(
                    backup,
                    new HashSet<string>(resolvable.Keys, StringComparer.Ordinal),
                    present,
                    deadIds);
                unresolvedTotal += unresolved.Count;
                if (leftAlone.Count > 0)
                {
                    _logger.LogInformation(
                        "Collection restore: leaving {Count} member(s) of \"{Name}\" alone - still in the library, "
                        + "so they were removed from the collection on purpose: {Which}",
                        leftAlone.Count, backup.Name, string.Join(", ", leftAlone));
                }
                if (unresolved.Count > 0)
                {
                    _logger.LogWarning(
                        "Collection restore: cannot find {Count} member(s) of \"{Name}\" by name: {Which}",
                        unresolved.Count, backup.Name, string.Join(", ", unresolved));
                }

                if (toAdd.Count == 0)
                {
                    continue;
                }

                var ids = toAdd.Select(m => resolvable[CollectionBackup.MemberKey(m)].Id).ToList();
                try
                {
                    _collectionManager.AddToCollectionAsync(box.Id, ids).GetAwaiter().GetResult();
                    added += ids.Count;
                    _logger.LogInformation(
                        "Collection restore: put {Count} member(s) back into \"{Name}\": {Which}",
                        ids.Count, backup.Name, string.Join(", ", toAdd.Select(m => m.Title)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Collection restore: could not add members to \"{Name}\"", backup.Name);
                }
            }

            _logger.LogInformation(
                "Collection restore: {Files} backup(s), {Collections} collections, {Added} member(s) added, "
                + "{Created} collection(s) recreated, {Unresolved} member(s) not found by name",
                files.Count, scanned, added, created, unresolvedTotal);
            progress?.Report(100);
        }

        /// <summary>
        /// The key an item is looked up by: its name for a series or a film, and its series plus the
        /// season number for a season (whose own name is just "Season 1").
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <returns>The key, or null for a type the restore does not handle.</returns>
        private static string? KeyFor(BaseItem item)
        {
            if (item is Season season && season.IndexNumber.HasValue)
            {
                return CollectionBackup.NameKey(season.SeriesName) + "|s"
                    + season.IndexNumber.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (item is Series || item is Movie)
            {
                return CollectionBackup.NameKey(item.Name);
            }

            return null;
        }
    }
}
