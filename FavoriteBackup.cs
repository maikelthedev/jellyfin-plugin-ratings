using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Renders a user's favourites (stars) as a human-readable backup, and works out what a restore
    /// should do with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A favourite is a row in Jellyfin's user data keyed by ITEM ID. Move a series to another
    /// library root, or rename it, and Jellyfin re-imports it as a new item with a new id: the star
    /// is gone, silently, for every user who had given it one. Nothing in Jellyfin restores it,
    /// because the id it belonged to no longer exists.
    /// </para>
    /// <para>
    /// So this records what survives such a move - name, type, year, series and PATH - per user, in a
    /// form a person can read and diff. Names are matched with the same rules as collections
    /// (<see cref="CollectionBackup.NameKey"/>), because the same apostrophe/dash/case differences
    /// run through this library.
    /// </para>
    /// </remarks>
    public static class FavoriteBackup
    {
        /// <summary>
        /// The key a favourite is matched by: the title, or the series plus season number for a
        /// season (the same rule collections use).
        /// </summary>
        /// <param name="favorite">The backed-up favourite.</param>
        /// <returns>The key.</returns>
        public static string KeyFor(StarredItem favorite) =>
            CollectionBackup.MemberKey(favorite.Title, favorite.Type, favorite.Series);

        /// <summary>
        /// The backup as JSON: two-space indented, stable key order, the item list first - so a
        /// human diff shows what changed and nothing else.
        /// </summary>
        /// <param name="backup">The user's favourites.</param>
        /// <returns>JSON text, newline-terminated.</returns>
        public static string Json(StarredList backup)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"user\": ").Append(Str(backup.User)).Append(",\n");
            sb.Append("  \"userId\": ").Append(Str(backup.UserId)).Append(",\n");
            sb.Append("  \"backedUpUtc\": ").Append(Str(backup.BackedUpUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"favoriteCount\": ").Append(backup.Items.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"missingCount\": ").Append(backup.Items.Count(i => i.Missing).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"favorites\": [\n");
            for (var i = 0; i < backup.Items.Count; i++)
            {
                var f = backup.Items[i];
                sb.Append("    { \"order\": ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append(", \"title\": ").Append(Str(f.Title))
                  .Append(", \"type\": ").Append(Str(f.Type))
                  .Append(", \"year\": ").Append(f.Year.HasValue ? f.Year.Value.ToString(CultureInfo.InvariantCulture) : "null")
                  .Append(", \"series\": ").Append(Str(f.Series))
                  .Append(", \"path\": ").Append(Str(f.Path))
                  .Append(", \"id\": ").Append(Str(f.Id));
                if (f.Missing)
                {
                    sb.Append(", \"missing\": true");
                }

                sb.Append(" }").Append(i == backup.Items.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>
        /// The same backup as Markdown, for reading: one heading per user and one line per favourite
        /// with the path that survives a move.
        /// </summary>
        /// <param name="backup">The user's favourites.</param>
        /// <returns>Markdown text, newline-terminated.</returns>
        public static string Markdown(StarredList backup)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(backup.User).Append(" — favourites\n\n");
            sb.Append("- user id: `").Append(backup.UserId ?? "?").Append("`\n");
            sb.Append("- backed up: ").Append(backup.BackedUpUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("\n");
            sb.Append("- favourites: ").Append(backup.Items.Count.ToString(CultureInfo.InvariantCulture));
            var missing = backup.Items.Count(i => i.Missing);
            if (missing > 0)
            {
                sb.Append("  (**").Append(missing.ToString(CultureInfo.InvariantCulture)).Append(" with a dead id**)");
            }

            sb.Append("\n\n");
            if (backup.Items.Count == 0)
            {
                sb.Append("_No favourites._\n");
                return sb.ToString();
            }

            foreach (var f in backup.Items)
            {
                sb.Append("- ").Append(f.Missing ? "**(missing)** " : string.Empty).Append(f.Title);
                if (f.Year.HasValue)
                {
                    sb.Append(" (").Append(f.Year.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                if (!string.IsNullOrEmpty(f.Series))
                {
                    sb.Append(" — _").Append(f.Series).Append('_');
                }

                sb.Append("\n  `").Append(f.Path ?? "(no path)").Append("`\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The index over every user, with each one's count - the first thing to look at when a star
        /// has quietly disappeared.
        /// </summary>
        /// <param name="users">Every user's favourites.</param>
        /// <param name="backedUpUtc">When the run happened.</param>
        /// <returns>Markdown text, newline-terminated.</returns>
        public static string IndexMarkdown(IEnumerable<StarredList> users, DateTime backedUpUtc)
        {
            var list = users.ToList();
            var sb = new StringBuilder();
            sb.Append("# Jellyfin favourites — backup\n\n");
            sb.Append("Backed up ").Append(backedUpUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            sb.Append(" by the Ratings plugin's \"Back up favourites\" task.\n\n");
            sb.Append("A favourite is user data keyed by **item id**, so a title that moves or is re-imported loses every star it had, ");
            sb.Append("silently. These files are the record of who had starred what - names and paths, which survive the move.\n\n");
            sb.Append("| user | favourites | dead links | state |\n|---|---:|---:|---|\n");
            foreach (var u in list)
            {
                var missing = u.Items.Count(i => i.Missing);
                sb.Append("| ").Append(u.User).Append(" | ").Append(u.Items.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(missing.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(u.Items.Count == 0 ? "**none**" : missing > 0 ? "needs attention" : "ok")
                  .Append(" |\n");
            }

            sb.Append('\n').Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(" user(s), ");
            sb.Append(list.Sum(u => u.Items.Count).ToString(CultureInfo.InvariantCulture)).Append(" favourites total.\n");
            return sb.ToString();
        }

        /// <summary>
        /// Reads a backup file back into a user's favourites.
        /// </summary>
        /// <param name="json">The file's text.</param>
        /// <returns>The backup, or null when the file is not one of ours.</returns>
        public static StarredList? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json!);
                var root = doc.RootElement;
                var user = root.TryGetProperty("user", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(user))
                {
                    return null;
                }

                var items = new List<StarredItem>();
                if (root.TryGetProperty("favorites", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in list.EnumerateArray())
                    {
                        items.Add(new StarredItem(
                            Str(m, "title") ?? "(unnamed)",
                            Str(m, "type"),
                            m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                            Str(m, "series"),
                            Str(m, "path"),
                            Str(m, "id"),
                            m.TryGetProperty("missing", out var mi) && mi.ValueKind == JsonValueKind.True));
                    }
                }

                var when = Str(root, "backedUpUtc");
                return new StarredList(
                    user!,
                    Str(root, "userId"),
                    DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : default,
                    items);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Works out what a restore should put back, without touching Jellyfin.
        /// </summary>
        /// <param name="backup">The user's backed-up favourites.</param>
        /// <param name="resolvableKeys">Keys that resolve to a live item, by name.</param>
        /// <param name="favoritedKeys">Keys the user has starred right now.</param>
        /// <param name="deadIds">Ids the backup recorded that no longer resolve to an item.</param>
        /// <returns>Favourites to re-add, names that could not be resolved, and un-stars left alone.</returns>
        public static (List<StarredItem> ToAdd, List<string> Unresolved, List<string> LeftAlone) Plan(
            StarredList backup,
            ISet<string> resolvableKeys,
            ISet<string> favoritedKeys,
            ISet<string> deadIds)
        {
            var toAdd = new List<StarredItem>();
            var unresolved = new List<string>();
            var leftAlone = new List<string>();
            var queued = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in backup.Items)
            {
                if (f.Missing)
                {
                    unresolved.Add(f.Title + " (only an id was known)");
                    continue;
                }

                var key = KeyFor(f);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (favoritedKeys.Contains(key))
                {
                    continue;   // already starred
                }

                // Id still resolves, but the user has no star on it: they took it off on purpose.
                if (!string.IsNullOrEmpty(f.Id) && !deadIds.Contains(f.Id!))
                {
                    leftAlone.Add(f.Title);
                    continue;
                }

                if (!resolvableKeys.Contains(key))
                {
                    unresolved.Add(f.Title);
                    continue;
                }

                if (queued.Add(key))
                {
                    toAdd.Add(f);
                }
            }

            return (toAdd, unresolved, leftAlone);
        }

        /// <summary>
        /// Compares a user's favourites against the previous backup: what they lost and gained.
        /// A loss is the thing worth shouting about - it means a star disappeared.
        /// </summary>
        /// <param name="previousJson">The previous backup, or null.</param>
        /// <param name="current">The current favourites.</param>
        /// <returns>The lost and gained keys (paths, or titles when there is no path).</returns>
        public static (List<string> Lost, List<string> Gained) Diff(string? previousJson, StarredList current)
        {
            var lost = new List<string>();
            var gained = new List<string>();
            var before = Parse(previousJson);
            if (before is null)
            {
                return (lost, gained);
            }

            var was = before.Items.Select(KeyOfItem).ToList();
            var now = current.Items.Select(KeyOfItem).ToList();
            lost.AddRange(was.Where(w => !now.Contains(w, StringComparer.OrdinalIgnoreCase)));
            gained.AddRange(now.Where(n => !was.Contains(n, StringComparer.OrdinalIgnoreCase)));
            return (lost, gained);
        }

        /// <summary>
        /// One line for <c>CHANGES.md</c>: whose favourites changed, by how much, and what moved.
        /// </summary>
        /// <param name="current">The user's current favourites.</param>
        /// <param name="lost">Favourites that disappeared.</param>
        /// <param name="gained">Favourites that appeared.</param>
        /// <returns>A Markdown list item, newline-terminated.</returns>
        public static string ChangeLine(StarredList current, IEnumerable<string> lost, IEnumerable<string> gained)
        {
            var lostList = lost.ToList();
            var gainedList = gained.ToList();
            var sb = new StringBuilder();
            sb.Append("- ").Append(current.BackedUpUtc.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture));
            sb.Append(" **").Append(current.User).Append("** now has ")
              .Append(current.Items.Count.ToString(CultureInfo.InvariantCulture)).Append(" favourite(s)");
            if (lostList.Count > 0)
            {
                sb.Append(" — lost: ").Append(string.Join(", ", lostList.Select(Short)));
            }

            if (gainedList.Count > 0)
            {
                sb.Append(lostList.Count > 0 ? ";" : " —").Append(" added: ").Append(string.Join(", ", gainedList.Select(Short)));
            }

            sb.Append('\n');
            return sb.ToString();
        }

        private static string KeyOfItem(StarredItem f) => string.IsNullOrEmpty(f.Path) ? f.Title : f.Path!;

        private static string Short(string key)
        {
            var trimmed = key.Replace("\\", "/", StringComparison.Ordinal).TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static string Str(string? value) => value is null ? "null" : "\"" + Escape(value) + "\"";

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// One starred item, as recorded in the backup.
    /// </summary>
    /// <param name="Title">The item's title.</param>
    /// <param name="Type">Its Jellyfin type (Series, Movie, Season…).</param>
    /// <param name="Year">Production year, when known.</param>
    /// <param name="Series">The series it belongs to, for an episode or season.</param>
    /// <param name="Path">Its path - the part that survives a move, and what a rebuild can use.</param>
    /// <param name="Id">Its Jellyfin id today. This is what the star is attached to, and what changes.</param>
    /// <param name="Missing">True when the star points at an id that no longer resolves to an item.</param>
    public record StarredItem(string Title, string? Type, int? Year, string? Series, string? Path, string? Id, bool Missing);

    /// <summary>
    /// One user's favourites at one moment.
    /// </summary>
    /// <param name="User">The user's name.</param>
    /// <param name="UserId">The user's Jellyfin id.</param>
    /// <param name="BackedUpUtc">When this snapshot was taken.</param>
    /// <param name="Items">Their favourited items.</param>
    public record StarredList(string User, string? UserId, DateTime BackedUpUtc, IReadOnlyList<StarredItem> Items);
}
