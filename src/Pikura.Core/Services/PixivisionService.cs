using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pikura.Core.Http;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>
/// Best-effort HTML scraper for pixivision.net (Pixiv's editorial "spotlight" site).
/// There is no supported public API for pixivision content — the legacy App API
/// <c>/v1/spotlight/articles</c> endpoint only covers metadata for the old "pixiv
/// Spotlight" predecessor and requires an OAuth refresh token most users never
/// configure — so this talks to the public, unauthenticated pixivision.net pages
/// directly. No login/cookies are required. Selectors are tied to pixivision's
/// current markup and may need updating if the site redesigns.
/// </summary>
public sealed partial class PixivisionService
{
    private const string BaseUrl = "https://www.pixivision.net";

    private readonly PixivHttpClientFactory _httpFactory;
    private readonly ILogger<PixivisionService> _logger;

    public PixivisionService(PixivHttpClientFactory httpFactory, ILogger<PixivisionService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public const string SlugAll = "";

    /// <summary>pixivision's own content categories, grouped exactly as shown on the site's nav
    /// bar (Explore / Create / Discover). The unfiltered "All" feed is ungrouped.</summary>
    public static readonly IReadOnlyList<PixivisionCategoryGroup> CategoryGroups =
    [
        new(null, null, [new(SlugAll, "All")]),
        new("Explore", "#3B82F6",
        [
            new("illustration", "Illustration"),
            new("manga", "Manga"),
            new("novels", "Novel"),
        ]),
        new("Create", "#10B981",
        [
            new("how-to-draw", "How to Draw"),
            new("draw-step-by-step", "Making"),
            new("textures", "Material"),
        ]),
        new("Discover", "#F97316",
        [
            new("interview", "Interview"),
            new("column", "Column"),
            new("news", "News"),
        ]),
    ];

    /// <summary>Flattened view of <see cref="CategoryGroups"/>, kept for lookups/defaults.</summary>
    public static readonly IReadOnlyList<PixivisionCategory> Categories =
        CategoryGroups.SelectMany(g => g.Items).ToList();

    /// <summary>
    /// Fetches one page of the article feed, optionally filtered to a single content category
    /// (see <see cref="Categories"/>). Page 1 is the feed root; subsequent pages use pixivision's
    /// <c>?p=N</c> pagination. <see cref="PixivisionArticlePage.HasNextPage"/> reflects whether the
    /// site's own pager widget advertises a "next" page.
    /// </summary>
    public async Task<PixivisionArticlePage> GetArticlesAsync(string? category = null, int page = 1, CancellationToken ct = default)
    {
        var basePath = string.IsNullOrEmpty(category) ? $"{BaseUrl}/en/" : $"{BaseUrl}/en/c/{category}/";
        var url = page <= 1 ? basePath : $"{basePath}?p={page}";
        var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
        var result = new PixivisionArticlePage { Page = page };
        if (html == null) return result;

        var matches = ArticleListItemRegex().Matches(html);
        var seen = new HashSet<long>();
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (!long.TryParse(m.Groups["id"].Value, out var id) || !seen.Add(id)) continue;

            // Scan the segment between this article's title link and the next one (or end of
            // document) for its tag list and publish date — both live in a "footer" block that
            // follows the title in the markup, in a fixed order per card.
            var segmentStart = m.Index;
            var segmentEnd = i + 1 < matches.Count ? matches[i + 1].Index : Math.Min(html.Length, segmentStart + 4000);
            var segment = html.Substring(segmentStart, Math.Max(0, segmentEnd - segmentStart));

            var tags = TagRegex().Matches(segment)
                .Select(t => WebUtility.HtmlDecode(t.Groups["tag"].Value))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();

            DateTime? published = null;
            var dateMatch = DateRegex().Match(segment);
            if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups["date"].Value, out var d))
                published = d;

            result.Items.Add(new PixivisionArticleSummary
            {
                Id = id,
                Title = WebUtility.HtmlDecode(m.Groups["title"].Value.Trim()),
                ThumbnailUrl = WebUtility.HtmlDecode(m.Groups["thumb"].Value.Trim()),
                Tags = tags,
                PublishedDate = published
            });
        }

        result.HasNextPage = NextPageRegex().IsMatch(html);
        result.MonthlyRanking = ExtractSection(html, RankingSectionRegex(), SidebarCardRegex());
        result.Featured = ExtractSection(html, FeaturedSectionRegex(), SidebarCardRegex());
        return result;
    }

    /// <summary>Parses a section of the page bounded by <paramref name="sectionRegex"/> (capturing
    /// a "body" group) for article cards matching <paramref name="cardRegex"/> (capturing "id",
    /// "thumb" and "title" groups). Shared by pixivision's sidebar widgets ("Monthly Ranking",
    /// "Featured") and its per-article "related articles" widgets.</summary>
    private static List<PixivisionArticleSummary> ExtractSection(string html, Regex sectionRegex, Regex cardRegex)
    {
        var list = new List<PixivisionArticleSummary>();
        var sectionMatch = sectionRegex.Match(html);
        if (!sectionMatch.Success) return list;

        var body = sectionMatch.Groups["body"].Value;
        var seen = new HashSet<long>();
        foreach (Match m in cardRegex.Matches(body))
        {
            if (!long.TryParse(m.Groups["id"].Value, out var id) || !seen.Add(id)) continue;
            list.Add(new PixivisionArticleSummary
            {
                Id = id,
                Title = WebUtility.HtmlDecode(m.Groups["title"].Value.Trim()),
                ThumbnailUrl = WebUtility.HtmlDecode(m.Groups["thumb"].Value.Trim())
            });
        }
        return list;
    }

    /// <summary>Scrapes a single article page for its title, body text and embedded Pixiv artworks.</summary>
    public async Task<PixivisionArticleDetail?> GetArticleAsync(long articleId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/en/a/{articleId}";
        var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
        if (html == null) return null;

        var detail = new PixivisionArticleDetail { Id = articleId };

        var titleMatch = OgTitleRegex().Match(html);
        detail.Title = titleMatch.Success
            ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim())
            : $"Article {articleId}";

        var eyecatchMatch = EyecatchRegex().Match(html);
        if (eyecatchMatch.Success)
            detail.EyecatchUrl = WebUtility.HtmlDecode(eyecatchMatch.Groups["src"].Value.Trim());

        // Regular articles only ever produce plain-text paragraphs, but interview-style articles
        // ("Artist's Spotlight" etc.) interleave section headings, interviewer questions and
        // interviewee answers (the latter sometimes with an avatar image) using entirely
        // different markup. All four block types are scraped independently and then merged back
        // into document order by match position, since each one's regex only matches its own
        // block and none of them overlap.
        var blocks = new List<(int Index, PixivisionParagraph Block)>();

        foreach (Match p in ParagraphRegex().Matches(html))
        {
            var (text, links) = ExtractTextAndLinks(p.Groups["body"].Value);
            if (!string.IsNullOrWhiteSpace(text) || links.Count > 0)
                blocks.Add((p.Index, new PixivisionParagraph { Text = text, Links = links }));
        }

        // Interview articles end with a "Check out past Artist's Spotlight interviews!"-style
        // heading immediately followed by a small grid of recommended article cards embedded
        // directly in the body — gather those cards up front so they can be attached to their
        // heading below instead of rendering as a dangling, empty section title.
        var inlineCardMatches = InlineRelatedCardRegex().Matches(html).Cast<Match>().OrderBy(m => m.Index).ToList();
        var headingMatches = HeadingRegex().Matches(html).Cast<Match>().OrderBy(m => m.Index).ToList();

        for (var hi = 0; hi < headingMatches.Count; hi++)
        {
            var h = headingMatches[hi];
            var text = StripHtml(h.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var rangeEnd = hi + 1 < headingMatches.Count ? headingMatches[hi + 1].Index : html.Length;
            var cardsInRange = inlineCardMatches.Where(m => m.Index > h.Index && m.Index < rangeEnd).ToList();
            List<PixivisionArticleSummary>? relatedCards = null;
            if (cardsInRange.Count > 0)
            {
                relatedCards = [];
                foreach (var cm in cardsInRange)
                {
                    if (!long.TryParse(cm.Groups["id"].Value, out var cardId)) continue;
                    relatedCards.Add(new PixivisionArticleSummary
                    {
                        Id = cardId,
                        Title = WebUtility.HtmlDecode(StripHtml(cm.Groups["title"].Value)),
                        ThumbnailUrl = WebUtility.HtmlDecode(cm.Groups["thumb"].Value.Trim())
                    });
                }
            }

            blocks.Add((h.Index, new PixivisionParagraph { Text = text, Kind = PixivisionParagraphKind.Heading, RelatedCards = relatedCards }));
        }

        foreach (Match q in QuestionRegex().Matches(html))
        {
            var (text, links) = ExtractTextAndLinks(q.Groups["body"].Value);
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add((q.Index, new PixivisionParagraph { Text = text, Links = links, Kind = PixivisionParagraphKind.Question }));
        }

        foreach (Match a in AnswerRegex().Matches(html))
        {
            var (text, links) = ExtractTextAndLinks(a.Groups["body"].Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var avatar = a.Groups["avatar"].Success ? WebUtility.HtmlDecode(a.Groups["avatar"].Value) : null;
                blocks.Add((a.Index, new PixivisionParagraph
                {
                    Text = text,
                    Links = links,
                    Kind = PixivisionParagraphKind.Answer,
                    AvatarUrl = avatar
                }));
            }
        }

        detail.RelatedLatest = ExtractSection(html, RelatedLatestSectionRegex(), RelatedCardRegex());
        detail.RelatedPopular = ExtractSection(html, RelatedPopularSectionRegex(), RelatedCardRegex());
        var tagMatch = RelatedTagNameRegex().Match(html);
        if (tagMatch.Success) detail.RelatedTagName = WebUtility.HtmlDecode(tagMatch.Groups["tag"].Value.Trim());

        var seenIllusts = new HashSet<string>();
        var workPositions = new List<(int Index, PixivisionFeaturedWork Work)>();
        foreach (Match w in FeaturedWorkRegex().Matches(html))
        {
            var illustId = w.Groups["illustId"].Value;
            if (string.IsNullOrEmpty(illustId) || !seenIllusts.Add(illustId)) continue;
            var work = new PixivisionFeaturedWork
            {
                IllustId = illustId,
                Title = WebUtility.HtmlDecode(w.Groups["title"].Value.Trim()),
                UserId = w.Groups["userId"].Value,
                UserName = WebUtility.HtmlDecode(w.Groups["userName"].Value.Trim()),
                ThumbnailUrl = WebUtility.HtmlDecode(w.Groups["thumb"].Value.Trim())
            };
            detail.Works.Add(work);
            workPositions.Add((w.Index, work));
            // Also drop a placeholder into the paragraph flow at the same document position, so
            // callers that want artworks interleaved with the reading flow (instead of a separate
            // gallery) can render them there.
            blocks.Add((w.Index, new PixivisionParagraph { Kind = PixivisionParagraphKind.Artwork, IllustId = illustId }));
        }

        foreach (var (_, block) in blocks.OrderBy(b => b.Index))
            detail.Paragraphs.Add(block);

        // Captions render as their own sibling block right after the artwork they describe —
        // attach each one to the nearest preceding work by document position.
        foreach (Match c in CaptionRegex().Matches(html))
        {
            var caption = StripHtml(c.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(caption)) continue;
            var target = workPositions.LastOrDefault(wp => wp.Index < c.Index);
            if (target.Work != null) target.Work.Caption = caption;
        }

        var tocMatch = TocSectionRegex().Match(html);
        if (tocMatch.Success)
        {
            foreach (Match e in TocEntryRegex().Matches(tocMatch.Groups["body"].Value))
            {
                var entry = StripHtml(e.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(entry))
                    detail.TableOfContents.Add(entry);
            }
        }

        var profileMatch = ProfileSectionRegex().Match(html);
        if (profileMatch.Success)
        {
            var body = profileMatch.Groups["body"].Value;
            var profile = new PixivisionProfile();

            var avatarMatch = ProfileAvatarRegex().Match(body);
            if (avatarMatch.Success) profile.AvatarUrl = WebUtility.HtmlDecode(avatarMatch.Groups["avatar"].Value);

            var nameMatch = ProfileNameRegex().Match(body);
            if (nameMatch.Success) profile.Name = StripHtml(nameMatch.Groups["name"].Value);

            var bioMatch = ProfileBioRegex().Match(body);
            if (bioMatch.Success) profile.Bio = StripHtml(bioMatch.Groups["bio"].Value);

            var (_, links) = ExtractTextAndLinks(body);
            profile.Links = links;

            if (!string.IsNullOrWhiteSpace(profile.Name) || !string.IsNullOrWhiteSpace(profile.Bio))
                detail.Profile = profile;
        }

        return detail;
    }

    private async Task<string?> GetHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("pixivision request failed: {Url} -> {Status}", url, resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pixivision request failed: {Url}", url);
            return null;
        }
    }

    private static string StripHtml(string html)
    {
        // Interview answers use sibling <div>-per-line instead of <p>, so their boundaries need
        // collapsing to newlines the same way <br> already is, or consecutive lines would run
        // together with no separation once tags are stripped.
        var withBreaks = DivCloseRegex().Replace(BrTagRegex().Replace(html, "\n"), "\n");
        var stripped = AnyTagRegex().Replace(withBreaks, string.Empty);
        return WebUtility.HtmlDecode(stripped).Trim();
    }

    /// <summary>Extracts a block's plain-text body and any embedded hyperlinks, stripping whole
    /// <c>&lt;a&gt;</c> tags out of the text first so link text doesn't appear twice (once
    /// inline, unclickable, and again as a clickable chip). Shared by every article-body block
    /// type (paragraph, question, answer).</summary>
    private static (string Text, List<PixivisionParagraphLink> Links) ExtractTextAndLinks(string raw)
    {
        var links = new List<PixivisionParagraphLink>();
        foreach (Match a in ParagraphLinkRegex().Matches(raw))
        {
            var linkText = StripHtml(a.Groups["text"].Value);
            var linkUrl = WebUtility.HtmlDecode(a.Groups["url"].Value);
            if (!string.IsNullOrWhiteSpace(linkText) && !string.IsNullOrWhiteSpace(linkUrl))
                links.Add(new PixivisionParagraphLink { Text = linkText, Url = linkUrl });
        }
        var text = StripHtml(ParagraphLinkRegex().Replace(raw, string.Empty));
        return (text, links);
    }

    // Matches both the hero card (aec__ classes) and regular feed cards (arc__ classes) in the
    // "Latest" article feed. The \k<id> backreference guarantees the thumbnail and title link
    // belong to the same article even though other unrelated links (category badges, etc.) sit
    // between them in the minified markup.
    [GeneratedRegex(
        """href="/en/a/(?<id>\d+)"data-gtm-action="ClickImage"[^>]*>.*?background-image:\s*url\((?<thumb>[^)]+)\).*?<h2 class="(?:aec|arc)__title"><a href="/en/a/\k<id>"data-gtm-action="ClickTitle"[^>]*>(?<title>[^<]+)</a>""",
        RegexOptions.Singleline)]
    private static partial Regex ArticleListItemRegex();

    [GeneratedRegex("""<meta property="og:title" content="(?<title>[^"]+)">""")]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex("<img class=\"aie__image\" src=\"(?<src>[^\"]+)\"")]
    private static partial Regex EyecatchRegex();

    // Tag chip anchors are distinguished from the category badge anchor by their ClickTag
    // gtm-action (the category badge uses ClickCategory instead); data-gtm-label already carries
    // the decoded, human-readable tag text so there's no need to dig into the nested <div> text.
    [GeneratedRegex("data-gtm-action=\"ClickTag\"\\s*data-gtm-label=\"(?<tag>[^\"]*)\"")]
    private static partial Regex TagRegex();

    [GeneratedRegex("<time[^>]*datetime=\"(?<date>[^\"]*)\"")]
    private static partial Regex DateRegex();

    // The pager widget renders a "›" link with class="next" pointing at the following page;
    // its absence means the current page is the last one.
    [GeneratedRegex("<a[^>]*class=\"next\"")]
    private static partial Regex NextPageRegex();

    [GeneratedRegex(
        """data-gtm-category="Ranking Area">(?<body>.*?)</section>""",
        RegexOptions.Singleline)]
    private static partial Regex RankingSectionRegex();

    [GeneratedRegex(
        """data-gtm-category="Osusume Area">(?<body>.*?)</section>""",
        RegexOptions.Singleline)]
    private static partial Regex FeaturedSectionRegex();

    // Sidebar widget cards ("_article-summary-card") use a different, smaller layout than the
    // main feed cards: the title link/text sits after an unrelated category-badge link, so the
    // \k<id> backreference is needed to keep them paired correctly.
    [GeneratedRegex(
        """href="/en/a/(?<id>\d+)"data-gtm-action="ClickImage"[^>]*>.*?background-image:\s*url\((?<thumb>[^)]+)\).*?<a href="/en/a/\k<id>"class="asc__title-link"data-gtm-action="ClickTitle"[^>]*><p class="asc__title">(?<title>[^<]+)</p>""",
        RegexOptions.Singleline)]
    private static partial Regex SidebarCardRegex();

    [GeneratedRegex(
        """<div class="fab__paragraph _medium-editor-text">(?<body>.*?)</div>""",
        RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex("""<a[^>]*href="(?<url>[^"]*)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ParagraphLinkRegex();

    // Interview-article ("Artist's Spotlight" etc.) section heading, e.g.
    // <div class="article-item _feature-article-body__heading" id="..."><h3>Text</h3></div>
    [GeneratedRegex(
        """<div class="article-item _feature-article-body__heading"[^>]*><h3>(?<body>.*?)</h3>""",
        RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    // Recommended-article card embedded directly in the body (e.g. right after "Check out past
    // Artist's Spotlight interviews!" at the end of interview articles) — visually and
    // structurally distinct from the sidebar-style "Related Article Latest/Popular" widgets.
    [GeneratedRegex(
        """<div class="article-item _feature-article-body__article_card">.*?<div class="_thumbnail" style="background-image:\s*url\((?<thumb>[^)]+)\)"></div></a>.*?<h2 class="arc__title"><a href="/en/a/(?<id>\d+)"[^>]*>(?<title>.*?)</a></h2>.*?</article></div>""",
        RegexOptions.Singleline)]
    private static partial Regex InlineRelatedCardRegex();

    // Interview-article question — same "fab__paragraph _medium-editor-text" wrapper as a regular
    // paragraph, but with an extra trailing "question" class, so it needs its own regex to match
    // (and to distinguish it from a plain paragraph when rendering).
    [GeneratedRegex(
        """<div class="fab__paragraph _medium-editor-text question">(?<body>.*?)</div>""",
        RegexOptions.Singleline)]
    private static partial Regex QuestionRegex();

    // Interview-article answer — an optional avatar <img>, followed by the reply text as sibling
    // <div>-per-line elements (not <p>) inside "answer-text". The literal "</div></div></div>"
    // closes answer-text → answer → article-item in that fixed order, which is a safe boundary
    // since replies never contain further nested <div>s of their own.
    [GeneratedRegex(
        """<div class="answer fab__paragraph">(?:<img[^>]*src="(?<avatar>[^"]+)"[^>]*>)?<div class="answer-text _medium-editor-text">(?<body>.*?)</div></div></div>""",
        RegexOptions.Singleline)]
    private static partial Regex AnswerRegex();

    // Caption shown directly under an embedded artwork, e.g. "Tokkyu's original illustration".
    // Content is always a single <p>, never nested <div>s, so a lazy match to the first
    // "</div></div>" is a safe boundary.
    [GeneratedRegex(
        """<div class="article-item _feature-article-body__caption"><div class="fab__caption">(?<body>.*?)</div></div>""",
        RegexOptions.Singleline)]
    private static partial Regex CaptionRegex();

    // The "Index" table-of-contents widget on interview-style articles — a <ul> of <a> links to
    // in-page anchors. Entry order/text matches the article's Heading blocks 1:1.
    [GeneratedRegex(
        """<div class="article-item _feature-article-body__table_of_contents">.*?<ul>(?<body>.*?)</ul>""",
        RegexOptions.Singleline)]
    private static partial Regex TocSectionRegex();

    [GeneratedRegex("""<a[^>]*href="#[^"]*"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex TocEntryRegex();

    // Interviewee profile/bio card. Bounded by the next "article-item" sibling (or end of
    // document) rather than counting nested </div>s, since the card's internal markup nests
    // several levels deep (making-body > making-profile > profile-wrapper > profile-contents).
    [GeneratedRegex(
        """<div class="article-item _feature-article-body__profile">(?<body>.*?)(?=<div class="article-item |$)""",
        RegexOptions.Singleline)]
    private static partial Regex ProfileSectionRegex();

    [GeneratedRegex(""""<img src="(?<avatar>[^"]+)"""")]
    private static partial Regex ProfileAvatarRegex();

    [GeneratedRegex("""<ul><li>(?<name>[^<]*)</li>""")]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex(
        """<li class="_medium-editor-text">(?<bio>.*?)</li>""",
        RegexOptions.Singleline)]
    private static partial Regex ProfileBioRegex();

    // "Newest articles tagged X" — pixivision's own recency-based related-article widget.
    [GeneratedRegex(
        """data-gtm-category="Related Article Latest">(?<body>.*?)</ul></div>""",
        RegexOptions.Singleline)]
    private static partial Regex RelatedLatestSectionRegex();

    // "If you liked X, you will also love..." — pixivision's own recommendation widget.
    [GeneratedRegex(
        """data-gtm-category="Related Article Popular">(?<body>.*?)</ul></div>""",
        RegexOptions.Singleline)]
    private static partial Regex RelatedPopularSectionRegex();

    [GeneratedRegex(
        """<article class="_article-related-card-test"><a href="/en/a/(?<id>\d+)" class="arrct__thumbnail-container"[^>]*>.*?<img class="thm__image" src="(?<thumb>[^"]+)" alt="(?<title>[^"]*)" loading""",
        RegexOptions.Singleline)]
    private static partial Regex RelatedCardRegex();

    [GeneratedRegex(
        """<h3 class="rla__heading yellow"><a[^>]*>Newest articles tagged <span class="_article-heading-tag-name">(?<tag>[^<]*)</span>""")]
    private static partial Regex RelatedTagNameRegex();

    // Each embedded artwork renders as a `am__work` block containing a user link, a title link
    // to the artwork, a "by {artist}" link, and a thumbnail image — in that fixed order.
    [GeneratedRegex(
        """<div class="am__work">.*?pixiv\.net/users/(?<userId>\d+)\?[^"]*"[^>]*>.*?<h3 class="am__work__title"><a href="https://www\.pixiv\.net/artworks/(?<illustId>\d+)\?[^"]*"[^>]*>(?<title>[^<]*)</a></h3><p class="am__work__user-name">by <a[^>]*>(?<userName>[^<]*)</a></p>.*?<img src="(?<thumb>[^"]+)" class="am__work__illust""",
        RegexOptions.Singleline)]
    private static partial Regex FeaturedWorkRegex();

    [GeneratedRegex("""<br\s*/?>""")]
    private static partial Regex BrTagRegex();

    [GeneratedRegex("""</div>""")]
    private static partial Regex DivCloseRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTagRegex();
}
