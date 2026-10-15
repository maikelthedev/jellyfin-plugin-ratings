using System;
using System.Collections.Generic;
using System.Globalization;
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
