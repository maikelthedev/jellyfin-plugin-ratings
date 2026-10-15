// Standalone harness for the plugin's NfoMirror logic: no Jellyfin packages, no server.
//   dotnet run -c Release      (from /tmp/nfotest)
// Mirrors the vectors in the Python side's test_episodic_land.NfoNeverDropsForeignElements,
// so the two implementations of the same rule can be compared.
using System.Text;
using Jellyfin.Plugin.Ratings;
using Jellyfin.Plugin.Ratings.Models;

var failures = 0;
void Check(bool ok, string what, string? detail = null)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (ok || detail is null ? "" : "\n         " + detail));
    if (!ok) failures++;
}

string Nfo(string plot, string[] tags) =>
    "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" +
    "<tvshow>\n" +
    "  <title>Mated to My Alpha Enemy</title>\n" +
    $"  <plot>{plot}</plot>\n" +
    "<some_random_field>arbitrary value 12345</some_random_field>\n" +
    "  <spoiler>Jason and Andrew are rival alphas, & they know it.</spoiler>\n" +
    string.Concat(tags.Select(t => $"  <tag>{t}</tag>\n")) +
    "</tvshow>\n";

var user = Guid.Parse("80a5ff19-8758-4ae4-baa9-d575cea9d761");
var item = Guid.Parse("3b4bfead-197a-8d43-dfdd-a217b2190bd4");
var snapshot = new DateTime(2026, 10, 15, 19, 0, 0, DateTimeKind.Utc);
var entries = new List<MirroredEntry>
{
    new("maikel", 2, "I just need to hear that violin — then\nI know it's him.", null, snapshot),
    new("Kasia", 8.5, null, null, snapshot),
    new("vicente", 7, "A season rating lives in the series file, so it is labelled.", 1, snapshot)
};
var ratings = new List<UserRating>
{
    new() { Id = Guid.NewGuid(), UserId = user, ItemId = item, Rating = 2, ReviewText = "x", UpdatedAt = snapshot }
};

Console.WriteLine("== BuildBlock ==");
var block = NfoMirror.BuildBlock("k3ntas", snapshot, entries);
Check(block.Contains("<pluginratings source=\"k3ntas\" exported=\"2026-10-15T19:00:00Z\">"), "block carries source + exported");
Check(block.Contains("value=\"8.5\""), "half-star value formatted invariantly");
Check(block.Contains("user=\"maikel\""), "the reviewer is a NAME, not a guid");
Check(!block.Contains("itemId"), "no item ids: the file already says which show it is");
Check(block.Contains("user=\"vicente\" value=\"7.0\" season=\"1\""), "a season rating is labelled with its season");
Check(block.Contains("<review>I just need to hear that violin"), "review text written");
Check(block.Split("<review>").Length == 3, "one <review> per review, and only where there is one");
Check(System.Text.RegularExpressions.Regex.IsMatch(block, "user=\"Kasia\" value=\"8.5\"[^>]*/>"), "a review-less rating is self-closing (no empty <review>)");

Check(NfoMirror.BuildBlock("k3ntas", snapshot, entries) == NfoMirror.BuildBlock("k3ntas", snapshot, entries),
    "the block is a pure function of the data (same input -> same bytes)");
Check(!NfoMirror.BuildBlock("k3ntas", DateTime.MinValue, entries).Contains("exported="),
    "a default timestamp omits the exported attribute");

Console.WriteLine("== Merge ==");
var before = Nfo("short plot", new[] { "Romance", "9:16" });
var merged = NfoMirror.Merge(before, block);
Check(merged.Contains("<some_random_field>arbitrary value 12345</some_random_field>"), "hand-added field survives");
Check(merged.Contains("<spoiler>Jason and Andrew are rival alphas, & they know it.</spoiler>"), "<spoiler> survives verbatim");
Check(merged.Contains("<plot>short plot</plot>"), "Jellyfin's own elements untouched");
Check(merged.IndexOf("<pluginratings", StringComparison.Ordinal) < merged.LastIndexOf("</tvshow>", StringComparison.Ordinal),
    "block lands before the root close");
Check(merged.Split("<pluginratings").Length == 2, "exactly one block");

var twice = NfoMirror.Merge(merged, block);
Check(string.Equals(twice, merged, StringComparison.Ordinal), "merge is idempotent");

var replaced = NfoMirror.Merge(merged, NfoMirror.BuildBlock("k3ntas", snapshot, entries.Take(1)));
Check(replaced.Split("<pluginratings").Length == 2, "re-merging replaces, never appends");
Check(replaced.Contains("<spoiler>"), "replacement keeps foreign elements too");

Check(string.Equals(NfoMirror.Merge(before, NfoMirror.BuildBlock("k3ntas", snapshot, new List<MirroredEntry>())).Split("<pluginratings").Length, 2),
    "an empty rating set still yields one block");
Check(string.Equals(NfoMirror.Merge("no root here", block), "no root here", StringComparison.Ordinal),
    "a file with no root is left untouched (never damage)");
Check(string.Equals(NfoMirror.Merge("", block), block, StringComparison.Ordinal), "empty input gets the block");

Console.WriteLine("== Parse (the restore path) ==");
var parsed = NfoMirror.Parse(merged);
Check(parsed.Count == 3, $"all entries read back ({parsed.Count})");
var first = parsed.FirstOrDefault(r => r.User == "maikel");
Check(first is not null && Math.Abs(first.Rating - 2) < 0.001, "rating restored");
Check(first?.ReviewText is not null && first.ReviewText!.Contains("then") && first.ReviewText.Contains('\n'),
    "review restored with its newline", first?.ReviewText);
var seasonEntry = parsed.FirstOrDefault(r => r.User == "vicente");
Check(seasonEntry?.SeasonNumber == 1, "the season label round-trips", seasonEntry?.SeasonNumber.ToString());
Check(parsed.All(r => !r.User.Contains('\u0040')), "no ids leaked into the entries");

Console.WriteLine("== escaping / round-trip ==");
var nasty = new List<MirroredEntry> { new("A \"quoted\" & <name>", 5, "A & B <tag> \"quoted\" \u0007 bell", null, snapshot) };
var nastyBlock = NfoMirror.BuildBlock("k3ntas", snapshot, nasty);
Check(nastyBlock.Contains("&amp;") && nastyBlock.Contains("&lt;tag&gt;"), "ampersands and angle brackets escaped");
Check(!nastyBlock.Contains('\u0007'), "control characters stripped");
var nastyBack = NfoMirror.Parse(NfoMirror.Merge(before, nastyBlock)).Single();
Check(nastyBack.ReviewText == "A & B <tag> \"quoted\"  bell", "escaped review round-trips (control char stripped, rest exact)", nastyBack.ReviewText);

Console.WriteLine("== against the real NFO in this library ==");
var real = "../../../../tmp/case_demo/tvshow.nfo";
if (File.Exists(real))
{
    var text = File.ReadAllText(real);
    var outText = NfoMirror.Merge(text, block);
    var sp = System.Text.RegularExpressions.Regex.Match(text, "<spoiler>(.*?)</spoiler>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
    var sp2 = System.Text.RegularExpressions.Regex.Match(outText, "<spoiler>(.*?)</spoiler>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
    Check(sp.Length > 100 && sp == sp2, $"the real <spoiler> is byte-identical ({sp2.Length} chars)");
    var hand = "<some_random_field>arbitrary value 12345</some_random_field>";
    Check(outText.Contains(hand), "the real hand-added field survives");
    var stripped = outText.Replace(NfoMirror.Extract(outText) ?? "", "");
    Check(!stripped.Contains("<pluginratings"), "everything outside our block is untouched");
}
else
{
    Console.WriteLine("  (skipped: ../../../../tmp/case_demo/tvshow.nfo not present)");
}

// Optional completeness pass: read every mirrored NFO back with the same parser the
// restore path uses, and report what it can reconstruct.
if (args.Length >= 2 && args[0] == "--scan")
{
    var files = Directory.EnumerateFiles(args[1], "*.nfo", SearchOption.AllDirectories)
                         .Where(f => File.ReadAllText(f).Contains("<pluginratings"));
    var scanned = new List<MirroredEntry>();
    var bad = 0;
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in files)
    {
        var got = NfoMirror.Parse(File.ReadAllText(f));
        if (got.Count == 0) { bad++; Console.WriteLine("  no entries parsed from " + f); }
        scanned.AddRange(got);
        foreach (var g in got) ids.Add(g.User);
    }
    Console.WriteLine($"\n== scan {args[1]} ==");
    Console.WriteLine($"  NFOs with a block : {files.Count()}  (unreadable: {bad})");
    Console.WriteLine($"  ratings read back : {scanned.Count}  by {ids.Count} distinct reviewers");
    Console.WriteLine($"  with review text  : {scanned.Count(e => !string.IsNullOrEmpty(e.ReviewText))}");
    Console.WriteLine($"  ratings without a value in range: {scanned.Count(e => e.Rating < 0 || e.Rating > 10)}");
}


Console.WriteLine("== CollectionBackup ==");
var snap = new CollectionSnapshot("Power Couples", "4232cb992ce858f146aa247b6ee09d94",
    "/var/lib/jellyfin/data/collections/Power Couples [boxset]", snapshot,
    new List<CollectionMember>
    {
        new("Mated to My Alpha Enemy", "Series", 2026, null, "/mnt/Sandisk/jellyfin/series/Mated to My Alpha Enemy", "b96087b6", false),
        new("My Two Spy Daddies", "Series", 2026, null, "/mnt/Sandisk/jellyfin/series/My Two Spy Daddies", "d393f5b0", false),
        new("(missing item)", null, null, null, null, "deadbeef", true)
    });
var json = CollectionBackup.Json(snap);
Check(CollectionBackup.SafeFileName("Jani's watch list ") == "Jani-s watch list", "unsafe characters are replaced", CollectionBackup.SafeFileName("Jani's watch list "));
Check(CollectionBackup.SafeFileName(new string('x', 200)).Length <= 120, "long names are truncated");
Check(json == CollectionBackup.Json(snap), "the same snapshot always renders the same bytes");
Check(json.Contains("\"memberCount\": 3"), "json records the member count");
Check(json.Contains("/mnt/Sandisk/jellyfin/series/Mated to My Alpha Enemy"), "json records the PATH (what a rebuild needs)");
Check(json.Contains("\"missing\": true"), "a dead link is flagged, not dropped");
try
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    Check(doc.RootElement.GetProperty("members").GetArrayLength() == 3, "json is valid and complete");
}
catch (Exception ex) { Check(false, "json parses", ex.Message); }

var md = CollectionBackup.Markdown(snap);
Check(md.StartsWith("# Power Couples"), "markdown starts with the name");
Check(md.Contains("- Mated to My Alpha Enemy (2026)"), "markdown lists members with year");
Check(md.Contains("`/mnt/Sandisk/jellyfin/series/Mated to My Alpha Enemy`"), "markdown shows the path");
Check(md.Contains("**(missing)**"), "markdown flags a dead link");
Check(CollectionBackup.Markdown(snap with { Members = new List<CollectionMember>() }).Contains("_Empty._"), "an empty collection says so");
Check(CollectionBackup.IndexMarkdown(new[] { snap }, snapshot).Contains("| Power Couples | 3 | 1 | needs attention |"), "index row counts members and dead links",
    CollectionBackup.IndexMarkdown(new[] { snap }, snapshot).Split('\n').FirstOrDefault(l => l.Contains("Power Couples")));
var (lost0, gained0) = CollectionBackup.Diff(null, snap);
Check(lost0.Count == 0 && gained0.Count == 0, "no previous backup means no change to report");
var m1 = snap.Members[0];
var m2 = snap.Members[1];
var extra = new CollectionMember("Thrown Into Prison", "Series", 2026, null, "/mnt/Sandisk/jellyfin/series/Thrown Into Prison", "aaaa1111", false);
var three = snap with { Members = new List<CollectionMember> { m1, m2, extra } };
var two = snap with { Members = new List<CollectionMember> { m1, m2 } };
var (lost, gained) = CollectionBackup.Diff(CollectionBackup.Json(three), two);
Check(lost.Count == 1 && lost[0].Contains("Thrown Into Prison"), "a member that dropped out is reported as lost", string.Join(",", lost));
Check(gained.Count == 0, "dropping a member is not an addition");
var (gainedLost, gainedList) = CollectionBackup.Diff(CollectionBackup.Json(two), three);
Check(gainedList.Count == 1 && gainedLost.Count == 0, "a member that appears is reported as added", string.Join(",", gainedList));

// The real failure mode: the item is still in the collection, but its id no longer resolves
// (the show moved and was re-imported). That shows up as the PATH disappearing and a dead id
// appearing - which is the difference between "the user removed it" and "Jellyfin lost it".
var died = snap with { Members = new List<CollectionMember> { m1 with { Missing = true, Title = "(missing item)", Path = null, Id = "deadbeef" }, m2 } };
var (lost2, gained2) = CollectionBackup.Diff(CollectionBackup.Json(two), died);
Check(lost2.Count == 1 && lost2[0].Contains("Mated to My Alpha Enemy"), "an item whose id died is reported as lost", string.Join(",", lost2));
Check(gained2.Count == 1 && gained2[0] == "(missing item)", "and the dead link is reported, so you can see it was the id that changed", string.Join(",", gained2));
var (l2, g2) = CollectionBackup.Diff("{not json", snap);
Check(l2.Count == 0 && g2.Count == 0, "an unreadable previous backup does not break the run");
var line = CollectionBackup.ChangeLine(snap, new[] { "/mnt/x/My Two Spy Daddies" }, Array.Empty<string>());
Check(line.Contains("Power Couples") && line.Contains("3 member(s)") && line.Contains("My Two Spy Daddies"), "CHANGES.md line names the collection, the count and the loss", line.Trim());

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
