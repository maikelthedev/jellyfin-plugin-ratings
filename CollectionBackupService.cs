using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Scheduled task that writes every collection (Jellyfin BoxSet) to disk in a human-readable
    /// form: a JSON file and a Markdown file per collection, plus an index and a change log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A collection is a list of item IDS. Move a title to another library root or rename it and
    /// Jellyfin re-imports it as a new item with a new id - the link dangles, Jellyfin prunes it,
    /// and the collection quietly gets smaller with nothing to restore from. Recording the member
    /// list (name, type, year, series and PATH) is the only way back, because the path is what
    /// survives the move.
    /// </para>
    /// <para>
    /// Files are only rewritten when the member list actually changes, so a quiet run touches
    /// nothing - and each change also lands in <c>history/</c>, so yesterday's version of a
    /// shrunken collection is still there.
    /// </para>
    /// </remarks>
    public class CollectionBackupService : IScheduledTask
    {
        private readonly ILogger<CollectionBackupService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionBackupService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager, used to enumerate collections and members.</param>
        /// <param name="appPaths">Application paths (default destination).</param>
        public CollectionBackupService(
            ILogger<CollectionBackupService> logger,
            ILibraryManager libraryManager,
            IApplicationPaths appPaths)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _appPaths = appPaths;
        }

        /// <inheritdoc />
        public string Name => "Back up collections";

        /// <inheritdoc />
        public string Key => "K3ntasCollectionBackup";

        /// <inheritdoc />
        public string Description =>
            "Writes every collection to the backup folder as .json (diffable) and .md (readable), with each "
            + "member's name, type, year, series and path. A collection is a list of item ids, so a title that "
            + "moves loses its place; this is the record that lets you rebuild it. Changes are kept in history/.";

        /// <inheritdoc />
        public string Category => "K3ntas Ratings";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = (TimeSpan.FromHours(4) + TimeSpan.FromMinutes(15)).Ticks
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
                        _logger.LogError(ex, "Collection backup failed");
                    }
                },
                cancellationToken);
        }

        private void Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.BackupCollections)
            {
                _logger.LogInformation("Collection backup disabled (BackupCollections is off) — nothing to do");
                return;
            }

            var target = string.IsNullOrWhiteSpace(config.BackupCollectionsTo)
                ? "/mnt/sata3/jellyfin/collections"
                : config.BackupCollectionsTo!.Trim();
            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(target, "history"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Collection backup: cannot write to {Target}", target);
                return;
            }

            var boxes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

            var now = DateTime.UtcNow;
            var snapshots = new List<CollectionSnapshot>();
            var written = 0;
            var unchanged = 0;
            var shrank = 0;
            var deadLinks = 0;
            var done = 0;

            foreach (var box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(done * 100.0 / Math.Max(boxes.Count, 1));

                if (box is not BoxSet set)
                {
                    // Only a BoxSet has linked children; anything else in this query is not a collection.
                    continue;
                }

                var name = (box.Name ?? "collection").Trim();
                var members = new List<CollectionMember>();
                foreach (var link in set.LinkedChildren ?? Array.Empty<LinkedChild>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = link.ItemId.HasValue ? _libraryManager.GetItemById(link.ItemId.Value) : null;
                    if (item is null && !string.IsNullOrEmpty(link.Path))
                    {
                        // A path link: the item is resolved by path, which is exactly the thing that
                        // survives a move.
                        item = _libraryManager.FindByPath(link.Path, false);
                    }

                    if (item is null)
                    {
                        deadLinks++;
                        members.Add(new CollectionMember(
                            "(missing item)", null, null, null, link.Path,
                            link.ItemId?.ToString("N", CultureInfo.InvariantCulture), true));
                        continue;
                    }

                    var series = item switch
                    {
                        MediaBrowser.Controller.Entities.TV.Episode ep => ep.SeriesName,
                        MediaBrowser.Controller.Entities.TV.Season sn => sn.SeriesName,
                        _ => null
                    };
                    members.Add(new CollectionMember(
                        item.Name ?? "(unnamed)",
                        item.GetClientTypeName(),
                        item.ProductionYear,
                        series,
                        item.Path,
                        item.Id.ToString("N", CultureInfo.InvariantCulture),
                        false));
                }

                var snapshot = new CollectionSnapshot(name, box.Id.ToString("N", CultureInfo.InvariantCulture), box.Path, now, members);
                snapshots.Add(snapshot);

                var stem = CollectionBackup.SafeFileName(name);
                var jsonPath = Path.Combine(target, stem + ".json");
                var mdPath = Path.Combine(target, stem + ".md");
                var previous = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null;
                var (lost, gained) = CollectionBackup.Diff(previous, snapshot);
                var changed = previous is null || lost.Count > 0 || gained.Count > 0;

                if (!changed)
                {
                    unchanged++;
                }
                else
                {
                    var json = CollectionBackup.Json(snapshot);
                    try
                    {
                        File.WriteAllText(jsonPath, json);
                        File.WriteAllText(mdPath, CollectionBackup.Markdown(snapshot));
                        var historyName = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        var historyDir = Path.Combine(target, "history", historyName);
                        Directory.CreateDirectory(historyDir);
                        File.WriteAllText(Path.Combine(historyDir, stem + ".json"), json);
                        File.AppendAllText(Path.Combine(target, "CHANGES.md"),
                            CollectionBackup.ChangeLine(snapshot, lost, gained));
                        written++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Collection backup: could not write {Path}", jsonPath);
                        continue;
                    }

                    if (lost.Count > 0)
                    {
                        shrank++;
                        _logger.LogWarning(
                            "Collection \"{Name}\" LOST {Count} member(s): {Lost}",
                            name, lost.Count, string.Join(", ", lost));
                    }

                    if (gained.Count > 0)
                    {
                        _logger.LogInformation(
                            "Collection \"{Name}\" gained {Count} member(s): {Gained}",
                            name, gained.Count, string.Join(", ", gained));
                    }
                }

                // Always, changed or not: the collection's own metadata belongs in the backup, and
                // CopyChanged also repairs the mode of a copy made before it was made readable.
                CopyChanged(Path.Combine(box.Path ?? string.Empty, "collection.xml"),
                    Path.Combine(target, stem + ".collection.xml"));

                if (members.Any(m => m.Missing))
                {
                    _logger.LogWarning(
                        "Collection \"{Name}\" holds {Count} link(s) that no longer resolve to an item: {Which}",
                        name,
                        members.Count(m => m.Missing),
                        string.Join(", ", members.Where(m => m.Missing).Select(m => m.Id)));
                }
            }

            // The index is rewritten every run: it is the one file that should say when the last
            // check happened, and it is tiny.
            try
            {
                File.WriteAllText(Path.Combine(target, "README.md"), CollectionBackup.IndexMarkdown(snapshots, now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Collection backup: could not write the index in {Target}", target);
            }

            _logger.LogInformation(
                "Collection backup: {Collections} collections, {Members} members — {Written} updated, "
                + "{Unchanged} unchanged, {Shrank} shrank, {Dead} dead link(s) -> {Target}",
                snapshots.Count, snapshots.Sum(s => s.Members.Count), written, unchanged, shrank, deadLinks, target);
            progress?.Report(100);
        }

        /// <summary>
        /// Copies a collection's own sidecar metadata (collection.xml) when it differs, so the
        /// backup carries the collection's description and not just its members.
        /// </summary>
        /// <param name="source">The file in Jellyfin's collections folder.</param>
        /// <param name="dest">Where the backup keeps it.</param>
        private static void CopyChanged(string source, string dest)
        {
            if (!File.Exists(source))
            {
                return;
            }

            var src = new FileInfo(source);
            var dst = new FileInfo(dest);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc)
            {
                // Up to date - but the mode still has to be right: a plain File.Copy of Jellyfin's
                // collections folder carries its 0600 over, which makes the backup unreadable to the
                // human it is written for.
                SetReadable(dest, dst);
                return;
            }

            File.Copy(source, dest, true);
            SetReadable(dest, new FileInfo(dest));
        }

        /// <summary>
        /// Makes a backed-up file readable (0644): the backup exists to be read by a person, and the
        /// originals in Jellyfin's data folder are 0600.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="info">Its FileInfo.</param>
        private static void SetReadable(string path, FileInfo info)
        {
            try
            {
                if ((info.UnixFileMode & (UnixFileMode.OtherRead | UnixFileMode.GroupRead)) != (UnixFileMode.OtherRead | UnixFileMode.GroupRead))
                {
                    File.SetUnixFileMode(path, info.UnixFileMode | UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
            }
            catch (PlatformNotSupportedException)
            {
                // Not a POSIX filesystem: nothing to fix.
            }
            catch (Exception)
            {
                // A mode is not worth failing the backup over.
            }
        }
    }
}
