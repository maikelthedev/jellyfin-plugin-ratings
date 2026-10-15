using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Data;
using Jellyfin.Plugin.Ratings.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Api
{
    /// <summary>
    /// Serves the OpenGraph preview for a shared review, in the same spirit as the Share Button
    /// plugin's <c>/ShareButton/Item/{id}</c>: crawlers get real OpenGraph HTML, humans are sent
    /// on to the item.
    /// </summary>
    /// <remarks>
    /// A review cannot be shared with its poster, title, stars and text as a link preview unless
    /// there is a server-rendered URL to point at: Jellyfin's detail page is a hash route, so a
    /// crawler never receives the item id at all. This endpoint is that URL. It is anonymous
    /// because Discord, WhatsApp and Telegram fetch it without a session - the same trade-off the
    /// Share Button plugin documents, and the reason it is worth stating: anyone holding the link
    /// can read that one review, and ids are not enumerable in practice (two GUIDs).
    /// </remarks>
    [ApiController]
    [Route("Ratings/Share")]
    public class ReviewShareController : ControllerBase
    {
        private readonly RatingsRepository _repository;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILogger<ReviewShareController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReviewShareController"/> class.
        /// </summary>
        /// <param name="repository">Ratings repository.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="applicationHost">Application host, for the server's display name.</param>
        /// <param name="logger">Logger instance.</param>
        public ReviewShareController(
            RatingsRepository repository,
            IUserManager userManager,
            ILibraryManager libraryManager,
            IServerApplicationHost applicationHost,
            ILogger<ReviewShareController> logger)
        {
            _repository = repository;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _applicationHost = applicationHost;
            _logger = logger;
        }

        private static PluginConfiguration Config =>
            Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <summary>
        /// The page a shared review points at. Crawlers get OpenGraph tags; humans are sent on to
        /// the item's detail page.
        /// </summary>
        /// <param name="reviewerUserId">The user who wrote the review.</param>
        /// <param name="itemId">The reviewed item.</param>
        /// <returns>OpenGraph HTML, or a redirect for a human.</returns>
        [HttpGet("{reviewerUserId}/{itemId}")]
        [AllowAnonymous]
        public ActionResult GetPreview([FromRoute] string reviewerUserId, [FromRoute] string itemId)
        {
            if (!TryGuid(reviewerUserId, out var userId) || !TryGuid(itemId, out var mediaItemId))
            {
                return BadRequest("invalid id");
            }

            var view = Build(userId, mediaItemId);
            if (view == null)
            {
                return NotFound("no review for that user and item");
            }

            // A human clicking the link wants the show, not a stub page; a crawler wants the HTML.
            // A 302 keeps the two apart without any JavaScript, which unfurlers do not run.
            if (!IsCrawler(Request.Headers.UserAgent.ToString()))
            {
                return Redirect(view.WebUrl);
            }

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
            html.Append("<meta charset=\"utf-8\">\n");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            html.Append($"<title>{Escape(view.Title)}</title>\n");
            html.Append($"<meta name=\"description\" content=\"{Escape(view.Description)}\">\n");

            html.Append($"<meta property=\"og:site_name\" content=\"{Escape(view.SiteName)}\">\n");
            html.Append("<meta property=\"og:type\" content=\"article\">\n");
            html.Append($"<meta property=\"og:title\" content=\"{Escape(view.OgTitle)}\">\n");
            html.Append($"<meta property=\"og:description\" content=\"{Escape(view.Description)}\">\n");
            if (view.ImageUrl != null)
            {
                html.Append($"<meta property=\"og:image\" content=\"{Escape(view.ImageUrl)}\">\n");
                html.Append($"<meta property=\"og:image:alt\" content=\"{Escape(view.Title)}\">\n");
            }

            html.Append($"<meta property=\"og:url\" content=\"{Escape(view.ShareUrl)}\">\n");

            // Posters are portrait, so the small square card is the one that does not letterbox.
            html.Append("<meta name=\"twitter:card\" content=\"summary\">\n");
            html.Append($"<meta name=\"twitter:title\" content=\"{Escape(view.OgTitle)}\">\n");
            html.Append($"<meta name=\"twitter:description\" content=\"{Escape(view.Description)}\">\n");
            if (view.ImageUrl != null)
            {
                html.Append($"<meta name=\"twitter:image\" content=\"{Escape(view.ImageUrl)}\">\n");
            }

            html.Append("</head>\n<body>\n");
            html.Append($"<article style=\"max-width:640px;margin:2rem auto;font:16px/1.5 system-ui,sans-serif\">\n");
            if (view.ImageUrl != null)
            {
                html.Append($"<img src=\"{Escape(view.ImageUrl)}\" alt=\"{Escape(view.Title)}\" style=\"float:left;width:160px;margin:0 1rem 1rem 0;border-radius:8px\">\n");
            }

            html.Append($"<h1 style=\"margin:0 0 .25rem\">{Escape(view.Title)}</h1>\n");
            html.Append($"<p style=\"margin:0 0 .75rem;color:#b8860b;font-size:22px\">{Escape(view.Stars)} <span style=\"font-size:15px;color:#666\">{Escape(view.Score)}</span></p>\n");
            if (view.ReviewText.Length > 0)
            {
                html.Append($"<blockquote style=\"margin:0 0 1rem;padding-left:1rem;border-left:3px solid #ddd\">{Escape(view.ReviewText)}</blockquote>\n");
            }

            html.Append($"<p style=\"color:#666\">Review by {Escape(view.Username)} on {Escape(view.SiteName)}</p>\n");
            html.Append($"<p><a href=\"{Escape(view.WebUrl)}\">Open in Jellyfin</a></p>\n");
            html.Append("</article>\n</body>\n</html>\n");

            Response.Headers["Cache-Control"] = "public, max-age=300";
            return Content(html.ToString(), "text/html; charset=utf-8");
        }

        /// <summary>
        /// JSON for the client, so the share button can build the link without guessing.
        /// </summary>
        /// <param name="reviewerUserId">The user who wrote the review.</param>
        /// <param name="itemId">The reviewed item.</param>
        /// <returns>The share summary.</returns>
        [HttpGet("{reviewerUserId}/{itemId}/info")]
        [Authorize]
        public ActionResult GetInfo([FromRoute] string reviewerUserId, [FromRoute] string itemId)
        {
            if (!TryGuid(reviewerUserId, out var userId) || !TryGuid(itemId, out var mediaItemId))
            {
                return BadRequest("invalid id");
            }

            var view = Build(userId, mediaItemId);
            if (view == null)
            {
                return NotFound();
            }

            return Ok(new
            {
                Title = view.Title,
                Username = view.Username,
                Rating = view.Rating,
                Stars = view.Stars,
                ReviewText = view.ReviewText,
                ShareUrl = view.ShareUrl,
                WebUrl = view.WebUrl,
                ImageUrl = view.ImageUrl
            });
        }

        /// <summary>
        /// Everything the card and the JSON need, resolved once.
        /// </summary>
        private ReviewView? Build(Guid userId, Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);

            // A rating is filed against the item's rating target - the season, for a vertical drama -
            // never against the series or episode the button happened to be pressed on, so a share
            // started outside the season has to look the review up on the season. Asking for the id it
            // was handed is what made the link 404 from the series and episode pages.
            var target = item != null ? RatingTargets.Resolve(_libraryManager, item).Item : null;
            var (candidates, pageId) = ShareResolver.Resolve(itemId, target?.Id, RatingTargets.SeriesIdOf(item));

            UserRating? rating = null;
            foreach (var candidate in candidates)
            {
                rating = _repository.GetUserRating(userId, candidate);
                if (rating != null)
                {
                    break;
                }
            }

            if (rating == null)
            {
                return null;
            }

            // The snapshot fields exist exactly so a rating stays meaningful after its item leaves
            // the library, so the card never depends on the item still being there.
            var title = FirstNonEmpty(rating.Title, item?.Name, "Jellyfin")!;
            var username = _userManager.GetUserById(userId)?.Username ?? "someone";
            var reviewText = Flatten(rating.ReviewText);
            var stars = Stars(rating.Rating, out var filled);
            var score = rating.Rating.ToString("0.#", CultureInfo.InvariantCulture) + "/10";

            var baseUrl = BaseUrl();

            // Only advertise an image that can actually be fetched; a 404 gives a crawler nothing
            // and no other symptom at all. Prefer Jellyfin's own poster while the item exists, and
            // fall back to the poster snapshot kept with the rating.
            string? imageUrl = PosterFor(target, baseUrl)
                ?? PosterFor(item, baseUrl);
            if (imageUrl == null && !string.IsNullOrWhiteSpace(rating.PosterUrl))
            {
                imageUrl = rating.PosterUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? rating.PosterUrl
                    : baseUrl + rating.PosterUrl;
            }

            // The description is what a chat client shows under the poster: stars first so the
            // rating reads even when the review is truncated to a couple of lines.
            var quote = reviewText.Length > 0 ? reviewText : null;
            var description = quote == null
                ? $"{stars} {score} — review by {username}"
                : $"{stars} {score} — “{Truncate(quote, 280)}” — {username}";

            var siteName = string.IsNullOrWhiteSpace(_applicationHost?.FriendlyName)
                ? "Jellyfin"
                : _applicationHost!.FriendlyName;

            return new ReviewView
            {
                Title = title,
                OgTitle = $"{title} · {stars} {score}",
                Description = description,
                Stars = stars,
                Score = score,
                Rating = rating.Rating,
                ReviewText = reviewText,
                Username = username,
                ImageUrl = imageUrl,
                ShareUrl = $"{baseUrl}/Ratings/Share/{userId:N}/{pageId:N}",
                WebUrl = $"{baseUrl}/web/#/details?id={pageId:N}",
                SiteName = siteName
            };
        }

        /// <summary>
        /// A Jellyfin poster URL for an item, when it has one.
        /// </summary>
        /// <param name="item">The item, or null.</param>
        /// <param name="baseUrl">The public base URL.</param>
        /// <returns>The URL, or null.</returns>
        private static string? PosterFor(MediaBrowser.Controller.Entities.BaseItem? item, string baseUrl)
        {
            return item != null && item.GetImageInfo(ImageType.Primary, 0) != null
                ? $"{baseUrl}/Items/{item.Id:N}/Images/Primary?maxHeight=900&quality=90"
                : null;
        }

        /// <summary>
        /// Five stars, filled to the nearest half-star's worth of a 1-10 score. Uses the block
        /// glyphs every unfurler renders (unlike emoji, which some strip from previews).
        /// </summary>
        private static string Stars(double rating, out int filled)
        {
            filled = Math.Clamp((int)Math.Round(Math.Clamp(rating, 0, 10) / 2.0, MidpointRounding.AwayFromZero), 0, 5);
            return new string('★', filled) + new string('☆', 5 - filled);
        }

        private static bool TryGuid(string value, out Guid id)
        {
            return Guid.TryParseExact(value?.Replace("-", string.Empty, StringComparison.Ordinal), "N", out id);
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max).TrimEnd() + "…";
        }

        /// <summary>
        /// The public base URL, from configuration if set, otherwise from the request, honouring
        /// the headers a TLS-terminating reverse proxy sets (the Share Button plugin's rule).
        /// </summary>
        private string BaseUrl()
        {
            var configured = Config.PublicBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.TrimEnd('/');
            }

            var scheme = FirstForwarded("X-Forwarded-Proto") ?? Request.Scheme;
            var host = FirstForwarded("X-Forwarded-Host") ?? Request.Host.Value;
            return $"{scheme}://{host}";
        }

        private string? FirstForwarded(string name)
        {
            if (!Request.Headers.TryGetValue(name, out var values))
            {
                return null;
            }

            var first = values.ToString().Split(',')[0].Trim();
            return string.IsNullOrEmpty(first) ? null : first;
        }

        /// <summary>
        /// Whether the User-Agent looks like a link-preview fetcher.
        /// </summary>
        private static bool IsCrawler(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return false;
            }

            string[] bots =
            {
                "facebookexternalhit", "facebookcatalog", "twitterbot", "discordbot",
                "telegrambot", "whatsapp", "slackbot", "linkedinbot", "pinterest",
                "redditbot", "embedly", "quora link preview", "showyoubot", "outbrain",
                "vkShare", "W3C_Validator", "Googlebot", "bingbot", "Applebot",
                "DuckDuckBot", "YandexBot", "Baiduspider", "ia_archiver", "SkypeUriPreview",
                "nuzzel", "Snapchat", "Iframely", "AhrefsBot", "SemrushBot", "curl", "Wget"
            };

            return bots.Any(b => userAgent.Contains(b, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Collapse all whitespace (including newlines) to single spaces: a raw newline inside an
        /// attribute splits it and strict crawlers then see nothing.
        /// </summary>
        private static string Flatten(string? value)
        {
            return string.Join(" ", (value ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string Escape(string? value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private sealed class ReviewView
        {
            public string Title { get; set; } = string.Empty;

            public string OgTitle { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public string Stars { get; set; } = string.Empty;

            public string Score { get; set; } = string.Empty;

            public double Rating { get; set; }

            public string ReviewText { get; set; } = string.Empty;

            public string Username { get; set; } = string.Empty;

            public string? ImageUrl { get; set; }

            public string ShareUrl { get; set; } = string.Empty;

            public string WebUrl { get; set; } = string.Empty;

            public string SiteName { get; set; } = string.Empty;
        }
    }
}
