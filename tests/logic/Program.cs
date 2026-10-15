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


Console.WriteLine("== the restore's name matching (name, never path or id) ==");
Check(CollectionBackup.NameKey("GOOD BOYS DON_T SLEEP WITH VAMPIRES") == CollectionBackup.NameKey("Good Boys Don't Sleep with Vampires"), "apostrophes fold");
Check(CollectionBackup.NameKey("Groom of the Beast\u2011King") == CollectionBackup.NameKey("Groom of the Beast-King"), "typographic dashes fold");
Check(CollectionBackup.NameKey("Corazón Salvaje") == CollectionBackup.NameKey("Corazon Salvaje"), "diacritics fold");
Check(CollectionBackup.NameKey("WTF\uff01I\u2018m Not Your Cinderella") == CollectionBackup.NameKey("WTF! I'm Not Your Cinderella"), "fullwidth punctuation folds");
Check(CollectionBackup.NameKey("A Rhapsody for the Crown Prince") != CollectionBackup.NameKey("Bully at School"), "different titles stay different");
Check(CollectionBackup.NameKey(null) == "" && CollectionBackup.NameKey("   ") == "", "an empty name has an empty key");

var seasonMember = new CollectionMember("Season 1", "Season", null, "My Two Spy Daddies", "/x/My Two Spy Daddies/Season 01", "abc123", false);
Check(CollectionBackup.MemberKey(seasonMember) == CollectionBackup.NameKey("My Two Spy Daddies") + "|s1",
    "a season keys on its series plus its number, not on 'Season 1'", CollectionBackup.MemberKey(seasonMember));
Check(CollectionBackup.SeasonNumberOf(seasonMember) == 1, "the season number comes from the title");
Check(CollectionBackup.SeasonNumberOf(snap.Members[0]) == null, "a series is not a season");

var back = CollectionBackup.Parse(CollectionBackup.Json(snap));
Check(back is not null && back.Name == "Power Couples" && back.Members.Count == 3, "a backup round-trips through Parse");
Check(back!.Members[2].Missing, "the missing flag survives the round trip");
Check(back.Members[0].Path!.Contains("Mated to My Alpha Enemy"), "the path survives the round trip");
Check(CollectionBackup.Parse("{") is null && CollectionBackup.Parse("") is null, "garbage does not parse as a collection");

var keys = new HashSet<string>(StringComparer.Ordinal) { CollectionBackup.MemberKey(m1), CollectionBackup.MemberKey(seasonMember) };
var already = new HashSet<string>(StringComparer.Ordinal) { CollectionBackup.MemberKey(m1) };
// m1's recorded id is gone (moved/re-imported); seasonMember's is gone too; m2 is not in the library.
var dead = new HashSet<string>(StringComparer.Ordinal) { m1.Id!, seasonMember.Id!, m2.Id! };
var planBackup = snap with { Members = new List<CollectionMember> { m1, m2, seasonMember, snap.Members[2] } };
var (toRestore, notFound, alone) = CollectionBackup.Plan(planBackup, keys, already, dead);
Check(toRestore.Count == 1 && toRestore[0].Title == "Season 1", "a member whose id died is restored, by name", string.Join(",", toRestore.Select(m => m.Title)));
Check(notFound.Any(u => u.Contains("My Two Spy Daddies")), "a member the library no longer has is reported, not skipped", string.Join(" | ", notFound));
Check(notFound.Any(u => u.Contains("only an id was known")), "a dead link says why it cannot be restored", string.Join(" | ", notFound));
Check(alone.Count == 0, "nothing is left alone when every missing member's id is dead");

// The other half of the rule: an id that STILL resolves, for an item that is not in the
// collection, was removed there by a person - restoring it would fight them.
var (deliberate, _, leftAlone) = CollectionBackup.Plan(planBackup with { Members = new List<CollectionMember> { m2 } }, keys, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
Check(deliberate.Count == 0, "a deliberately removed member is NOT restored");
Check(leftAlone.Count == 1 && leftAlone[0].Contains("My Two Spy Daddies"), "and it is reported as left alone", string.Join(",", leftAlone));
var (nothingToDo, _, _) = CollectionBackup.Plan(planBackup, keys, new HashSet<string>(keys, StringComparer.Ordinal), dead);
Check(nothingToDo.Count == 0, "a collection that already has everything restores nothing");


Console.WriteLine("== favourites (same rules, per user) ==");
var fav = new StarredList("kasia", "80a5ff19", snapshot, new List<StarredItem>
{
    new("Mated to My Alpha Enemy", "Series", 2026, null, "/mnt/Sandisk/jellyfin/series/Mated to My Alpha Enemy", "aaaa1111", false),
    new("Season 1", "Season", null, "My Two Spy Daddies", "/mnt/Sandisk/jellyfin/series/My Two Spy Daddies/Season 01", "bbbb2222", false),
    new("(missing item)", null, null, null, null, "cccc3333", true)
});
var favJson = FavoriteBackup.Json(fav);
Check(favJson.Contains("\"user\": \"kasia\"") && favJson.Contains("\"favoriteCount\": 3"), "the backup is per user");
Check(favJson.Contains("/mnt/Sandisk/jellyfin/series/Mated to My Alpha Enemy"), "it records the PATH (what survives a move)");
Check(favJson.Contains("\"missing\": true"), "a detached star is flagged, not dropped");
var favBack = FavoriteBackup.Parse(favJson);
Check(favBack is not null && favBack.User == "kasia" && favBack.Items.Count == 3, "it round-trips through Parse");
Check(favBack!.Items[2].Missing, "the detached flag survives the round trip");
Check(FavoriteBackup.Markdown(fav).Contains("**(missing)**"), "markdown flags a detached star");
Check(FavoriteBackup.IndexMarkdown(new[] { fav }, snapshot).Contains("| kasia | 3 | 1 | needs attention |"), "index counts stars and detached ones");
Check(FavoriteBackup.KeyFor(fav.Items[1]) == CollectionBackup.NameKey("My Two Spy Daddies") + "|s1", "a season keys on its series plus number");
Check(FavoriteBackup.KeyFor(fav.Items[0]) == CollectionBackup.NameKey("Mated to My Alpha Enemy"), "a series keys on its name");
var favKeys = new HashSet<string>(StringComparer.Ordinal) { FavoriteBackup.KeyFor(fav.Items[0]), FavoriteBackup.KeyFor(fav.Items[1]) };
var deadFav = new HashSet<string>(StringComparer.Ordinal) { fav.Items[0].Id!, fav.Items[1].Id! };
var (reStar, favNotFound, favAlone) = FavoriteBackup.Plan(fav, favKeys, new HashSet<string>(StringComparer.Ordinal), deadFav);
Check(reStar.Count == 2 && favNotFound.Count == 1, "stars whose id died are put back; a detached one is reported", string.Join(",", favNotFound));
Check(favAlone.Count == 0, "nothing is left alone when every recorded id is dead");
var (noReStar, _, onlyAlone) = FavoriteBackup.Plan(fav with { Items = new List<StarredItem> { fav.Items[0] } }, favKeys, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
Check(noReStar.Count == 0 && onlyAlone.Count == 1, "a star taken off on purpose is NOT put back");
var (alreadyStarred, _, _) = FavoriteBackup.Plan(fav with { Items = new List<StarredItem> { fav.Items[0] } }, favKeys, favKeys, deadFav);
Check(alreadyStarred.Count == 0, "a star the user still has is not touched");
var (fl, fg) = FavoriteBackup.Diff(FavoriteBackup.Json(fav with { Items = new List<StarredItem> { fav.Items[0], fav.Items[1] } }), fav);
Check(fl.Count == 0 && fg.Count == 1, "a detached star appearing counts as a change", string.Join(",", fg));

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
