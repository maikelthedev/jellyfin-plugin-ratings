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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Scheduled task that writes every user's favourites to disk in a human-readable form: a JSON
    /// and a Markdown file per user, an index, a change log and a history folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A favourite is user data keyed by ITEM ID, so the same thing that empties collections silently
    /// removes stars: move a title to another library root, or rename it, and Jellyfin re-imports it
    /// with a new id. Jellyfin then parks the orphaned user data on a placeholder item (which records
    /// no name), and the star is unrecoverable - this library already has 202 of those. Recording the
    /// name and path per user, before the move, is the only way back.
    /// </para>
    /// <para>
    /// Files are only rewritten when a user's list actually changes, so a quiet run touches nothing.
    /// </para>
    /// </remarks>
    public class FavoriteBackupService : IScheduledTask
    {
        private readonly ILogger<FavoriteBackupService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IApplicationPaths _appPaths;

        /// <summary>
        /// Initializes a new instance of the <see cref="FavoriteBackupService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager, used to list each user's favourites.</param>
        /// <param name="userManager">User manager, used to enumerate users.</param>
        /// <param name="appPaths">Application paths.</param>
        public FavoriteBackupService(
            ILogger<FavoriteBackupService> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IApplicationPaths appPaths)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _appPaths = appPaths;
        }

        /// <inheritdoc />
        public string Name => "Back up favourites";

        /// <inheritdoc />
        public string Key => "K3ntasFavoriteBackup";

        /// <inheritdoc />
        public string Description =>
            "Writes every user's favourites to the backup folder as .json (diffable) and .md (readable), with "
            + "each item's name, type, year, series and path. A favourite is user data keyed by item id, so a "
            + "title that moves loses its stars; this is the record that lets them be put back. Changes are kept in history/.";

        /// <inheritdoc />
        public string Category => "K3ntas Ratings";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = (TimeSpan.FromHours(4) + TimeSpan.FromMinutes(20)).Ticks
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
                        _logger.LogError(ex, "Favourite backup failed");
                    }
                },
                cancellationToken);
        }

        private void Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.BackupFavorites)
            {
                _logger.LogInformation("Favourite backup disabled (BackupFavorites is off) — nothing to do");
                return;
            }

            var target = string.IsNullOrWhiteSpace(config.BackupFavoritesTo)
                ? "/mnt/sata3/jellyfin/favorites"
                : config.BackupFavoritesTo!.Trim();
            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(target, "history"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Favourite backup: cannot write to {Target}", target);
                return;
            }

            var users = JellyfinCompat.GetAllUsers(_userManager)
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var now = DateTime.UtcNow;
            var backups = new List<StarredList>();
            var written = 0;
            var unchanged = 0;
            var shrank = 0;
            var detached = 0;
            var done = 0;

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(done * 100.0 / Math.Max(users.Count, 1));

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = user,
                    IsFavorite = true,
                    Recursive = true
                });

                var favorites = new List<StarredItem>();
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var missing = string.IsNullOrEmpty(item.Path);
                    if (missing)
                    {
                        detached++;
                    }

                    var series = item switch
                    {
                        Episode ep => ep.SeriesName,
                        Season sn => sn.SeriesName,
                        _ => null
                    };
                    favorites.Add(new StarredItem(
                        missing ? "(missing item)" : item.Name ?? "(unnamed)",
                        missing ? null : item.GetClientTypeName(),
                        missing ? null : item.ProductionYear,
                        series,
                        item.Path,
                        item.Id.ToString("N", CultureInfo.InvariantCulture),
                        missing));
                }

                var backup = new StarredList(user.Username ?? "unknown", user.Id.ToString("N", CultureInfo.InvariantCulture), now, favorites);
                backups.Add(backup);

                var stem = CollectionBackup.SafeFileName(user.Username);
                var jsonPath = Path.Combine(target, stem + ".json");
                var mdPath = Path.Combine(target, stem + ".md");
                var previous = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null;
                var (lost, gained) = FavoriteBackup.Diff(previous, backup);
                var changed = previous is null || lost.Count > 0 || gained.Count > 0;

                if (!changed)
                {
                    unchanged++;
                }
                else
                {
                    try
                    {
                        var json = FavoriteBackup.Json(backup);
                        File.WriteAllText(jsonPath, json);
                        File.WriteAllText(mdPath, FavoriteBackup.Markdown(backup));
                        var historyDir = Path.Combine(target, "history", now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                        Directory.CreateDirectory(historyDir);
                        File.WriteAllText(Path.Combine(historyDir, stem + ".json"), json);
                        File.AppendAllText(Path.Combine(target, "CHANGES.md"),
                            FavoriteBackup.ChangeLine(backup, lost, gained));
                        written++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Favourite backup: could not write {Path}", jsonPath);
                        continue;
                    }

                    if (lost.Count > 0)
                    {
                        shrank++;
                        _logger.LogWarning(
                            "Favourites: {User} LOST {Count} star(s): {Lost}",
                            user.Username, lost.Count, string.Join(", ", lost));
                    }

                    if (gained.Count > 0)
                    {
                        _logger.LogInformation(
                            "Favourites: {User} starred {Count} new item(s): {Gained}",
                            user.Username, gained.Count, string.Join(", ", gained));
                    }
                }
            }

            try
            {
                File.WriteAllText(Path.Combine(target, "README.md"), FavoriteBackup.IndexMarkdown(backups, now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Favourite backup: could not write the index in {Target}", target);
            }

            _logger.LogInformation(
                "Favourite backup: {Users} user(s), {Favorites} favourite(s) — {Written} updated, {Unchanged} unchanged, "
                + "{Shrank} shrank, {Detached} already detached (unrecoverable), {Fallback} -> {Target}",
                backups.Count, backups.Sum(b => b.Items.Count), written, unchanged, shrank, detached, _appPaths.DataPath, target);
            progress?.Report(100);
        }
    }
}
