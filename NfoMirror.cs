using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ratings
{
    /// <summary>
    /// Mirrors the plugin's ratings and reviews into an item's NFO as an inert
    /// <c>&lt;pluginratings&gt;</c> element, and reads them back out again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why an inert element: Jellyfin's NFO reader skips elements it does not know, so the block
    /// never reaches the UI, and both of these libraries have no NFO metadata saver enabled, so
    /// Jellyfin never rewrites the file either - the element survives refreshes and API metadata
    /// writes. The plugin store stays authoritative; this is a backup that can be restored from.
    /// </para>
    /// <para>
    /// The block is written to be READ BY A PERSON: the reviewer appears by name, and there are no
    /// ids, because the show's identity is implied by the file the block lives in. The one case
    /// where that is not enough is a rating on a season - a season has no NFO of its own, so its
    /// rating sits in the series' file and says <c>season="1"</c> to distinguish itself:
    /// </para>
    /// <code>
    /// &lt;pluginratings source="k3ntas" exported="2026-10-13T18:40:54Z"&gt;
    ///   &lt;rating user="maikel" value="8.0" updatedAt="2026-10-13T18:40:54Z"&gt;
    ///     &lt;review&gt;Ooookay, what do we have here?&lt;/review&gt;
    ///   &lt;/rating&gt;
    ///   &lt;rating user="Kasia" value="7.0" season="1"/&gt;
    /// &lt;/pluginratings&gt;
    /// </code>
    /// <para>
    /// The merge is byte-surgical and never reserialises the document: only the plugin's own block
    /// is replaced. Everything else in the file is copied verbatim - Jellyfin's own elements,
    /// <c>&lt;spoiler&gt;</c> (the series digest the per-episode descriptions are generated from),
    /// hand-added fields, indentation, the XML declaration.
    /// </para>
    /// </remarks>
    public static class NfoMirror
    {
        /// <summary>The element carrying the mirror. Deliberately not a name Jellyfin knows.</summary>
        public const string ElementName = "pluginratings";

        private static readonly Regex OurBlockRegex = new Regex(
            @"[ \t]*<pluginratings\b.*?</pluginratings>[ \t]*\r?\n?",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex RatingRegex = new Regex(
            @"<rating\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</rating>)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ReviewRegex = new Regex(
            @"<review>(?<text>.*?)</review>", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex RootRegex = new Regex(
            @"<([A-Za-z_][\w:.-]*)[ \t>]", RegexOptions.Compiled);

        /// <summary>
        /// Index of the closing tag of the document root, or -1 when there is none.
        /// </summary>
        /// <remarks>
        /// Explicitly the ROOT element - the first element after the XML declaration - never "the
        /// last line that happens to end in &lt;/something&gt;": <c>&lt;tag&gt;40 Episodes&lt;/tag&gt;</c>
        /// and <c>&lt;uniqueid …&gt;123&lt;/uniqueid&gt;</c> end that way too, and anchoring on one of
        /// those puts the block in the middle of the file.
        /// </remarks>
        /// <param name="body">The NFO's text.</param>
        /// <returns>The index of the root's closing tag, or -1.</returns>
        public static int RootCloseIndex(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return -1;
            }

            var scan = body!.TrimStart().StartsWith("<?xml", StringComparison.Ordinal)
                ? AfterDeclaration(body)
                : body;
            var m = RootRegex.Match(scan);
            if (!m.Success)
            {
                return -1;
            }

            return body!.LastIndexOf("</" + m.Groups[1].Value + ">", StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the plugin's block: one entry per reviewer, reviews included.
        /// </summary>
        /// <param name="source">Value for the block's <c>source</c> attribute.</param>
        /// <param name="exportedUtc">
        /// The newest change among <paramref name="entries"/>, i.e. how current the backup is.
        /// Deliberately NOT the current time: the block has to be a pure function of the data, or
        /// every run rewrites every file and the task becomes a source of churn (mtime changes are
        /// what nudges Jellyfin into a refresh). Pass <see cref="DateTime.MinValue"/> to omit it.
        /// </param>
        /// <param name="entries">What to write.</param>
        /// <returns>The block's XML text.</returns>
        public static string BuildBlock(string source, DateTime exportedUtc, IEnumerable<MirroredEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("  <").Append(ElementName)
              .Append(" source=\"").Append(Escape(source)).Append('"');
            if (exportedUtc != default)
            {
                sb.Append(" exported=\"")
                  .Append(exportedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                  .Append('"');
            }

            sb.Append(">\n");
            foreach (var e in entries.OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.User, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("    <rating")
                  .Append(" user=\"").Append(Escape(e.User)).Append('"')
                  .Append(" value=\"").Append(e.Rating.ToString("0.0##", CultureInfo.InvariantCulture)).Append('"');
                if (e.SeasonNumber.HasValue)
                {
                    sb.Append(" season=\"").Append(e.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (e.UpdatedAt.HasValue && e.UpdatedAt.Value != default)
                {
                    sb.Append(" updatedAt=\"")
                      .Append(e.UpdatedAt.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                      .Append('"');
                }

                var review = Clean(e.ReviewText);
                if (string.IsNullOrEmpty(review))
                {
                    sb.Append("/>\n");
                }
                else
                {
                    sb.Append(">\n      <review>").Append(Escape(review!)).Append("</review>\n    </rating>\n");
                }
            }

            sb.Append("  </").Append(ElementName).Append(">\n");
            return sb.ToString();
        }

        /// <summary>
        /// Puts <paramref name="block"/> into <paramref name="existingNfo"/>, replacing the plugin's
        /// previous block if there is one and leaving every other byte of the file untouched.
        /// </summary>
        /// <param name="existingNfo">The NFO as it is on disk.</param>
        /// <param name="block">A block from <see cref="BuildBlock"/>.</param>
        /// <returns>The text to write, or the input unchanged when there is no root to anchor on.</returns>
        public static string Merge(string? existingNfo, string block)
        {
            if (string.IsNullOrEmpty(existingNfo))
            {
                return block;
            }

            if (OurBlockRegex.IsMatch(existingNfo!))
            {
                return OurBlockRegex.Replace(existingNfo!, block, 1);
            }

            var at = RootCloseIndex(existingNfo);
            return at < 0 ? existingNfo! : existingNfo!.Insert(at, block);
        }

        /// <summary>
        /// The plugin's block inside an NFO, or null when there is none.
        /// </summary>
        /// <param name="nfo">The NFO's text.</param>
        /// <returns>The matched block text, or null.</returns>
        public static string? Extract(string? nfo)
        {
            if (string.IsNullOrEmpty(nfo))
            {
                return null;
            }

            var m = OurBlockRegex.Match(nfo!);
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// Reads the ratings back out of a mirrored NFO (the restore path).
        /// </summary>
        /// <param name="nfo">The NFO's text, or the block on its own.</param>
        /// <returns>One entry per review found; empty when the file has no block.</returns>
        public static List<MirroredEntry> Parse(string? nfo)
        {
            var found = new List<MirroredEntry>();
            var block = Extract(nfo);
            if (block is null)
            {
                return found;
            }

            foreach (Match m in RatingRegex.Matches(block))
            {
                var attrs = m.Groups["attrs"].Value;
                var user = Unescape(Attr(attrs, "user") ?? string.Empty);
                var value = Attr(attrs, "value");
                if (string.IsNullOrEmpty(user) || value is null
                    || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
                {
                    continue;
                }

                int? season = int.TryParse(Attr(attrs, "season"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                    ? s
                    : null;
                var updated = Attr(attrs, "updatedAt");
                DateTime? when = DateTime.TryParse(updated, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                var reviewMatch = ReviewRegex.Match(m.Groups["body"].Value);
                found.Add(new MirroredEntry(
                    user, rating,
                    reviewMatch.Success ? Unescape(reviewMatch.Groups["text"].Value) : null,
                    season, when));
            }

            return found;
        }

        private static string AfterDeclaration(string body)
        {
            var at = body.IndexOf("?>", StringComparison.Ordinal);
            return at < 0 ? body : body.Substring(at + 2);
        }

        private static string? Attr(string attrs, string name)
        {
            var m = Regex.Match(attrs, name + "\\s*=\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string? Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var sb = new StringBuilder(text!.Length);
            foreach (var c in text)
            {
                if (c == '\n' || c == '\r' || c == '\t' || c >= ' ')
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string Escape(string text) => SecurityElement.Escape(Clean(text) ?? string.Empty) ?? string.Empty;

        private static string Unescape(string text) => text
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }

    /// <summary>
    /// One review as it is written into (and read back from) a mirrored NFO.
    /// </summary>
    /// <param name="User">The reviewer's name. A person reading the NFO should see a name.</param>
    /// <param name="Rating">The rating value.</param>
    /// <param name="ReviewText">The review, if one was written.</param>
    /// <param name="SeasonNumber">
    /// Set only when the rating is not on the item that owns the file: a season has no NFO of its
    /// own, so its rating lives in the series' file and is labelled with the season number. No item
    /// ids are stored anywhere - the file says which show it is.
    /// </param>
    /// <param name="UpdatedAt">When the rating was last changed.</param>
    public record MirroredEntry(string User, double Rating, string? ReviewText, int? SeasonNumber, DateTime? UpdatedAt);
}
