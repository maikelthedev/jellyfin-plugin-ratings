using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Scheduled task that puts back the stars a user has lost, using the backup written by
    /// <see cref="FavoriteBackupService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Favourites are resolved <b>by name</b>, never by id (which is what changes) or path (which is
    /// what moves), using the same matching rules as the collection restore.
    /// </para>
    /// <para>
    /// A star is only put back when the id the backup recorded NO LONGER resolves: an id that still
    /// resolves, with no star on it, was un-starred by a person, and re-starring it would fight them.
    /// It runs an hour and a quarter before the backup, so a star lost during the day is compared
    /// against last night's file - the one that still has it.
    /// </para>
    /// </remarks>
    public class FavoriteRestoreService : IScheduledTask
    {
        private readonly ILogger<FavoriteRestoreService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FavoriteRestoreService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager, used to resolve names to items.</param>
        /// <param name="userManager">User manager, used to resolve the user.</param>
        /// <param name="userDataManager">User data manager, used to set the star.</param>
        public FavoriteRestoreService(
            ILogger<FavoriteRestoreService> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
        }

        /// <inheritdoc />
        public string Name => "Restore favourites from the backup";

        /// <inheritdoc />
        public string Key => "K3ntasFavoriteRestore";

        /// <inheritdoc />
        public string Description =>
            "Puts back the stars a user has lost, resolving them BY NAME (ids change when Jellyfin re-imports a "
            + "title, paths change when it moves). Only re-stars items whose recorded id is gone, so a star you "
            + "took off on purpose stays off.";

        /// <inheritdoc />
        public string Category => "K3ntas Ratings";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = (TimeSpan.FromHours(4) + TimeSpan.FromMinutes(5)).Ticks
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
                        _logger.LogError(ex, "Favourite restore failed");
                    }
                },
                cancellationToken);
        }

        private void Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.RestoreFavorites)
            {
                _logger.LogInformation("Favourite restore disabled (RestoreFavorites is off) — nothing to do");
                return;
            }

            var dir = string.IsNullOrWhiteSpace(config.BackupFavoritesTo)
                ? "/mnt/sata3/jellyfin/favorites"
                : config.BackupFavoritesTo!.Trim();
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Favourite restore: no backup folder at {Dir}", dir);
                return;
            }

            var files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                _logger.LogInformation("Favourite restore: no backups in {Dir} yet", dir);
                return;
            }

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

            var resolvableKeys = new HashSet<string>(resolvable.Keys, StringComparer.Ordinal);
            var added = 0;
            var unresolvedTotal = 0;
            var leftAloneTotal = 0;
            var users = 0;
            var done = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(done * 100.0 / Math.Max(files.Count, 1));

                var backup = FavoriteBackup.Parse(File.ReadAllText(file));
                if (backup is null)
                {
                    _logger.LogWarning("Favourite restore: {File} is not a favourite backup", Path.GetFileName(file));
                    continue;
                }

                var user = ResolveUser(backup);
                if (user is null)
                {
                    _logger.LogWarning("Favourite restore: no user for {File} (was \"{User}\")", Path.GetFileName(file), backup.User);
                    continue;
                }

                users++;

                var starred = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = user,
                    IsFavorite = true,
                    Recursive = true
                }))
                {
                    var key = KeyFor(item);
                    if (!string.IsNullOrEmpty(key))
                    {
                        starred.Add(key!);
                    }
                }

                var deadIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in backup.Items)
                {
                    if (!string.IsNullOrEmpty(f.Id) && Guid.TryParse(f.Id, out var recorded)
                        && _libraryManager.GetItemById(recorded) is null)
                    {
                        deadIds.Add(f.Id!);
                    }
                }

                var (toAdd, unresolved, leftAlone) = FavoriteBackup.Plan(backup, resolvableKeys, starred, deadIds);
                unresolvedTotal += unresolved.Count;
                leftAloneTotal += leftAlone.Count;
                if (unresolved.Count > 0)
                {
                    _logger.LogWarning(
                        "Favourite restore: cannot find {Count} of {User}'s favourites by name: {Which}",
                        unresolved.Count, backup.User, string.Join(", ", unresolved));
                }

                if (leftAlone.Count > 0)
                {
                    _logger.LogInformation(
                        "Favourite restore: leaving {Count} of {User}'s favourites alone - the item still exists, "
                        + "so the star was removed on purpose: {Which}",
                        leftAlone.Count, backup.User, string.Join(", ", leftAlone));
                }

                foreach (var f in toAdd)
                {
                    var item = resolvable[FavoriteBackup.KeyFor(f)];
                    try
                    {
                        var data = _userDataManager.GetUserData(user, item);
                        if (data is null)
                        {
                            continue;
                        }

                        data.IsFavorite = true;
                        _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
                        added++;
                        _logger.LogInformation("Favourite restore: re-starred \"{Title}\" for {User}", item.Name, backup.User);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Favourite restore: could not star \"{Title}\" for {User}", item.Name, backup.User);
                    }
                }
            }

            _logger.LogInformation(
                "Favourite restore: {Files} backup(s), {Users} user(s), {Added} star(s) put back, "
                + "{Left} left alone, {Unresolved} not found by name",
                files.Count, users, added, leftAloneTotal, unresolvedTotal);
            progress?.Report(100);
        }

        /// <summary>
        /// Resolves the user a backup belongs to: by name, or by the id it recorded.
        /// </summary>
        /// <param name="backup">The backup.</param>
        /// <returns>The user, or null.</returns>
        private User? ResolveUser(StarredList backup)
        {
            var byName = JellyfinCompat.GetAllUsers(_userManager)
                .FirstOrDefault(u => string.Equals(u.Username, backup.User, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }

            return !string.IsNullOrEmpty(backup.UserId) && Guid.TryParse(backup.UserId, out var id)
                ? _userManager.GetUserById(id)
                : null;
        }

        /// <summary>
        /// The key an item is starred under: its name for a series or a film, its series plus the
        /// season number for a season.
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
