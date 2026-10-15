using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Renders a collection (Jellyfin BoxSet) as a human-readable backup, and reports what changed
    /// since the previous backup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: a collection is stored as a list of item IDS (<c>LinkedChildren</c>:
    /// ParentId / SortOrder / ChildId / ChildType - no path is kept). Move a series to another
    /// library root, or rename it, and Jellyfin re-imports it as a NEW item with a new id: the old
    /// link dangles, gets pruned, and the collection silently shrinks. Nothing in Jellyfin can
    /// rebuild it, because the id it pointed at is gone.
    /// </para>
    /// <para>
    /// So the backup records what survives such a move - name, type, year, series and PATH - which
    /// is the information needed to rebuild the collection, in a form a person can read, diff and
    /// grep. It is deliberately not a copy of the database.
    /// </para>
    /// </remarks>
    public static class CollectionBackup
    {
        /// <summary>
        /// A filename that is safe on every filesystem and still recognisable: the collection's
        /// name, with the characters a shared folder (or Windows client) would reject replaced.
        /// </summary>
        /// <param name="name">The collection name.</param>
        /// <returns>The file name stem (no extension).</returns>
        public static string SafeFileName(string? name)
        {
            var s = string.IsNullOrWhiteSpace(name) ? "collection" : name!;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' || c == '(' || c == ')'
                    ? c
                    : '-');
            }

            var clean = sb.ToString().Trim().TrimEnd('.');
            while (clean.Contains("  ", StringComparison.Ordinal))
            {
                clean = clean.Replace("  ", " ", StringComparison.Ordinal);
            }

            return clean.Length > 120 ? clean.Substring(0, 120).TrimEnd() : clean;
        }

        /// <summary>
        /// The backup as JSON: two-space indented, stable key order, the member list first - so a
        /// human diff shows what changed and nothing else.
        /// </summary>
        /// <param name="snapshot">The collection's current state.</param>
        /// <returns>JSON text, newline-terminated.</returns>
        public static string Json(CollectionSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"collection\": ").Append(Str(snapshot.Name)).Append(",\n");
            sb.Append("  \"id\": ").Append(Str(snapshot.Id)).Append(",\n");
            sb.Append("  \"folder\": ").Append(Str(snapshot.Folder)).Append(",\n");
            sb.Append("  \"backedUpUtc\": ").Append(Str(snapshot.BackedUpUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"memberCount\": ").Append(snapshot.Members.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"missingCount\": ").Append(snapshot.Members.Count(m => m.Missing).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"members\": [\n");
            for (var i = 0; i < snapshot.Members.Count; i++)
            {
                var m = snapshot.Members[i];
                sb.Append("    { \"order\": ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append(", \"title\": ").Append(Str(m.Title))
                  .Append(", \"type\": ").Append(Str(m.Type))
                  .Append(", \"year\": ").Append(m.Year.HasValue ? m.Year.Value.ToString(CultureInfo.InvariantCulture) : "null")
                  .Append(", \"series\": ").Append(Str(m.Series))
                  .Append(", \"path\": ").Append(Str(m.Path))
                  .Append(", \"id\": ").Append(Str(m.Id));
                if (m.Missing)
                {
                    sb.Append(", \"missing\": true");
                }

                sb.Append(" }").Append(i == snapshot.Members.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>
        /// The same backup as Markdown, for reading: what the collection is, and one line per
        /// member with the path that survives a move.
        /// </summary>
        /// <param name="snapshot">The collection's current state.</param>
        /// <returns>Markdown text, newline-terminated.</returns>
        public static string Markdown(CollectionSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(snapshot.Name).Append("\n\n");
            sb.Append("- id: `").Append(snapshot.Id ?? "?").Append("`\n");
            sb.Append("- folder: `").Append(snapshot.Folder ?? "?").Append("`\n");
            sb.Append("- backed up: ").Append(snapshot.BackedUpUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("\n");
            sb.Append("- members: ").Append(snapshot.Members.Count.ToString(CultureInfo.InvariantCulture));
            var missing = snapshot.Members.Count(m => m.Missing);
            if (missing > 0)
            {
                sb.Append("  (**").Append(missing.ToString(CultureInfo.InvariantCulture)).Append(" with a dead id**)");
            }

            sb.Append("\n\n");
            if (snapshot.Members.Count == 0)
            {
                sb.Append("_Empty._\n");
                return sb.ToString();
            }

            foreach (var m in snapshot.Members)
            {
                sb.Append("- ").Append(m.Missing ? "**(missing)** " : string.Empty).Append(m.Title);
                if (m.Year.HasValue)
                {
                    sb.Append(" (").Append(m.Year.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                if (!string.IsNullOrEmpty(m.Series))
                {
                    sb.Append(" — _").Append(m.Series).Append('_');
                }

                sb.Append("\n  `").Append(m.Path ?? "(no path)").Append("`\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The index over every collection, with each one's size - the first thing to look at when
        /// a collection has quietly shrunk.
        /// </summary>
        /// <param name="snapshots">All collections, in backup order.</param>
        /// <param name="backedUpUtc">When the run happened.</param>
        /// <returns>Markdown text, newline-terminated.</returns>
        public static string IndexMarkdown(IEnumerable<CollectionSnapshot> snapshots, DateTime backedUpUtc)
        {
            var list = snapshots.ToList();
            var sb = new StringBuilder();
            sb.Append("# Jellyfin collections — backup\n\n");
            sb.Append("Backed up ").Append(backedUpUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            sb.Append(" by the Ratings plugin's \"Back up collections\" task.\n\n");
            sb.Append("Each collection has its member list here as `.json` (the data, diffable) and `.md` (the same, readable). ");
            sb.Append("A collection is a list of item **ids** in Jellyfin, and changing a title's location or id prunes those links, ");
            sb.Append("so these files are the record of what a collection was supposed to contain.\n\n");
            sb.Append("| collection | members | dead links | state |\n|---|---:|---:|---|\n");
            foreach (var s in list)
            {
                var missing = s.Members.Count(m => m.Missing);
                sb.Append("| ").Append(s.Name).Append(" | ").Append(s.Members.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(missing.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(s.Members.Count == 0 ? "**empty**" : missing > 0 ? "needs attention" : "ok")
                  .Append(" |\n");
            }

            sb.Append('\n').Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(" collections, ");
            sb.Append(list.Sum(s => s.Members.Count).ToString(CultureInfo.InvariantCulture)).Append(" members total.\n");
            return sb.ToString();
        }

        /// <summary>
        /// Compares a collection against the previous backup: what it lost and what it gained.
        /// A loss is the thing worth shouting about, since that is the silent data loss.
        /// </summary>
        /// <param name="previousJson">The previous backup, or null when there is none.</param>
        /// <param name="current">The collection's current state.</param>
        /// <returns>The lost and gained member keys (paths, or titles for dead links).</returns>
        public static (List<string> Lost, List<string> Gained) Diff(string? previousJson, CollectionSnapshot current)
        {
            var lost = new List<string>();
            var gained = new List<string>();
            if (string.IsNullOrWhiteSpace(previousJson))
            {
                return (lost, gained);
            }

            var before = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(previousJson!);
                if (doc.RootElement.TryGetProperty("members", out var members))
                {
                    foreach (var m in members.EnumerateArray())
                    {
                        before.Add(KeyOf(m));
                    }
                }
            }
            catch (JsonException)
            {
                // An unreadable previous backup must not stop the new one from being written.
                return (lost, gained);
            }

            var now = current.Members.Select(m => KeyOf(m)).ToList();
            lost.AddRange(before.Where(b => !now.Contains(b, StringComparer.OrdinalIgnoreCase)));
            gained.AddRange(now.Where(n => !before.Contains(n, StringComparer.OrdinalIgnoreCase)));
            return (lost, gained);
        }

        /// <summary>
        /// One line for <c>CHANGES.md</c>: the timestamp, the collection, the size change and what
        /// moved - the trail that makes a shrink point at the title that caused it.
        /// </summary>
        /// <param name="snapshot">The collection's current state.</param>
        /// <param name="lost">Members that disappeared.</param>
        /// <param name="gained">Members that appeared.</param>
        /// <returns>A Markdown list item, newline-terminated.</returns>
        public static string ChangeLine(CollectionSnapshot snapshot, IEnumerable<string> lost, IEnumerable<string> gained)
        {
            var lostList = lost.ToList();
            var gainedList = gained.ToList();
            var sb = new StringBuilder();
            sb.Append("- ").Append(snapshot.BackedUpUtc.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture));
            sb.Append(" **").Append(snapshot.Name).Append("** now has ")
              .Append(snapshot.Members.Count.ToString(CultureInfo.InvariantCulture)).Append(" member(s)");
            if (lostList.Count > 0)
            {
                sb.Append(" — lost: ").Append(string.Join(", ", lostList.Select(Short)));
            }

            if (gainedList.Count > 0)
            {
                sb.Append(gainedList.Count > 0 && lostList.Count > 0 ? ";" : " —").Append(" added: ").Append(string.Join(", ", gainedList.Select(Short)));
            }

            sb.Append('\n');
            return sb.ToString();
        }

        private static string Short(string key)
        {
            var trimmed = key.Replace("\\", "/", StringComparison.Ordinal).TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static string KeyOf(CollectionMember m) => string.IsNullOrEmpty(m.Path) ? m.Title : m.Path!;

        private static string KeyOf(JsonElement m)
        {
            var path = m.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var title = m.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return string.IsNullOrEmpty(path) ? title ?? string.Empty : path!;
        }

        private static string Str(string? value) => value is null ? "null" : "\"" + JsonEscape(value) + "\"";

        private static string JsonEscape(string value)
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

        /// <summary>
        /// The comparison key for a title: what makes two spellings "the same show" when a restore
        /// looks a member up by NAME rather than by path or id.
        /// </summary>
        /// <remarks>
        /// Titles are matched by name because the path is exactly what changes when a title moves
        /// between library roots, and the id is exactly what changes when Jellyfin re-imports it.
        /// Normalising has to be generous: this library contains pairs that differ only in an
        /// apostrophe or a typographic dash ("GOOD BOYS DON_T SLEEP WITH VAMPIRES" vs "Good Boys
        /// Don't Sleep with Vampires", "Groom of the Beast‑King" with a non-breaking hyphen).
        /// Diacritics and case fold away, punctuation separates, and everything else is dropped.
        /// </remarks>
        /// <param name="name">A title.</param>
        /// <returns>The key: lowercase letters and digits only.</returns>
        public static string NameKey(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name!.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;   // the accent of é, ä, ñ…
                }

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// The key a restore uses for one member: the title for a series or a film, and the series
        /// plus its season number for a season (whose own title is just "Season 1").
        /// </summary>
        /// <param name="member">The backed-up member.</param>
        /// <returns>The key.</returns>
        public static string MemberKey(CollectionMember member) =>
            MemberKey(member.Title, member.Type, member.Series);

        /// <summary>
        /// The same key, from loose parts, so the favourites backup can use the identical matching
        /// rules without pretending a star is a collection member.
        /// </summary>
        /// <param name="title">The item's title.</param>
        /// <param name="type">Its Jellyfin type name.</param>
        /// <param name="series">Its series, for a season or episode.</param>
        /// <returns>The key.</returns>
        public static string MemberKey(string? title, string? type, string? series)
        {
            var season = SeasonNumberOf(type, title);
            return season.HasValue && !string.IsNullOrWhiteSpace(series)
                ? NameKey(series) + "|s" + season.Value.ToString(CultureInfo.InvariantCulture)
                : NameKey(title);
        }

        /// <summary>
        /// The season number a member refers to, when it is a season: taken from its "Season N" title.
        /// </summary>
        /// <param name="member">The backed-up member.</param>
        /// <returns>The number, or null.</returns>
        public static int? SeasonNumberOf(CollectionMember member) =>
            member.Missing ? null : SeasonNumberOf(member.Type, member.Title);

        /// <summary>
        /// The season number a member refers to, when it is a season: taken from its "Season N" title.
        /// </summary>
        /// <param name="type">Its Jellyfin type name.</param>
        /// <param name="title">Its title.</param>
        /// <returns>The number, or null.</returns>
        public static int? SeasonNumberOf(string? type, string? title)
        {
            if (!string.Equals(type, "Season", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var m = Regex.Match(title ?? string.Empty, @"(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        /// <summary>
        /// Reads a backup file back into a snapshot.
        /// </summary>
        /// <param name="json">The file's text.</param>
        /// <returns>The snapshot, or null when the file is not one of ours.</returns>
        public static CollectionSnapshot? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json!);
                var root = doc.RootElement;
                var name = root.TryGetProperty("collection", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                var members = new List<CollectionMember>();
                if (root.TryGetProperty("members", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in list.EnumerateArray())
                    {
                        members.Add(new CollectionMember(
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
                return new CollectionSnapshot(
                    name!,
                    Str(root, "id"),
                    Str(root, "folder"),
                    DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : default,
                    members);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Works out what a restore should do with one collection, without touching Jellyfin: the
        /// members whose item can be found again and is missing from the collection, and the ones
        /// that could not be found (which a person has to look at).
        /// </summary>
        /// <param name="backup">The backed-up collection.</param>
        /// <param name="resolvableKeys">
        /// Member keys (see <see cref="MemberKey"/>) that resolve to a live item - what the library
        /// has today, by name.
        /// </param>
        /// <param name="presentKeys">Member keys already in the collection, so they are not re-added.</param>
        /// <param name="deadIds">
        /// The ids this backup recorded that no longer resolve to an item. This is what separates the
        /// two reasons a member can be missing, and it is the whole point of the rule: an id that
        /// still exists but is no longer in the collection was removed there by a PERSON, and putting
        /// it back would fight them - while an id that is gone means the title was re-imported (moved,
        /// renamed) and the collection lost it without anyone asking.
        /// </param>
        /// <returns>Members to add, titles that could not be resolved, and removals left alone.</returns>
        public static (List<CollectionMember> ToAdd, List<string> Unresolved, List<string> LeftAlone) Plan(
            CollectionSnapshot backup,
            ISet<string> resolvableKeys,
            ISet<string> presentKeys,
            ISet<string> deadIds)
        {
            var toAdd = new List<CollectionMember>();
            var unresolved = new List<string>();
            var leftAlone = new List<string>();
            var queued = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in backup.Members)
            {
                if (m.Missing)
                {
                    // The backup only ever had an id for this one: there is no name to resolve by.
                    unresolved.Add(m.Title + " (only an id was known)");
                    continue;
                }

                var key = MemberKey(m);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (presentKeys.Contains(key))
                {
                    continue;
                }

                // An id that still resolves, for an item that is not in the collection, is a
                // deliberate removal.
                if (!string.IsNullOrEmpty(m.Id) && !deadIds.Contains(m.Id!))
                {
                    leftAlone.Add(m.Title);
                    continue;
                }

                if (!resolvableKeys.Contains(key))
                {
                    unresolved.Add(m.Title);
                    continue;
                }

                if (queued.Add(key))
                {
                    toAdd.Add(m);
                }
            }

            return (toAdd, unresolved, leftAlone);
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// One member of a collection, as recorded in the backup.
    /// </summary>
    /// <param name="Title">The item's title.</param>
    /// <param name="Type">The item's Jellyfin type (Series, Movie, Season…).</param>
    /// <param name="Year">Production year, when known.</param>
    /// <param name="Series">The series it belongs to, for an episode or season.</param>
    /// <param name="Path">Its path — the part that survives a move, and the only thing a rebuild can use.</param>
    /// <param name="Id">Its Jellyfin id today. This is what the collection actually stores, and it is what changes.</param>
    /// <param name="Missing">True when the collection holds an id that no longer resolves to an item.</param>
    public record CollectionMember(string Title, string? Type, int? Year, string? Series, string? Path, string? Id, bool Missing);

    /// <summary>
    /// One collection at one moment: its identity, where its folder is, and its members in order.
    /// </summary>
    /// <param name="Name">The collection's name.</param>
    /// <param name="Id">The collection's own Jellyfin id.</param>
    /// <param name="Folder">Its folder on disk (where collection.xml and folder.jpg live).</param>
    /// <param name="BackedUpUtc">When this snapshot was taken.</param>
    /// <param name="Members">Its members, in the collection's own order.</param>
    public record CollectionSnapshot(string Name, string? Id, string? Folder, DateTime BackedUpUtc, IReadOnlyList<CollectionMember> Members);
}
