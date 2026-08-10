namespace Pikura.Core.Models;

/// <summary>Lightweight summary of a pixivision.net article, as shown in article listing pages.</summary>
public sealed class PixivisionArticleSummary
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime? PublishedDate { get; set; }
}

/// <summary>One page of results from the article feed/category listing, with pagination info
/// scraped from pixivision's own pager widget.</summary>
public sealed class PixivisionArticlePage
{
    public List<PixivisionArticleSummary> Items { get; set; } = new();
    public int Page { get; set; } = 1;
    public bool HasNextPage { get; set; }

    /// <summary>pixivision's own "Monthly Ranking" sidebar widget, top-to-bottom order = rank.
    /// Identical on every page of a given category, so callers only need to read it once.</summary>
    public List<PixivisionArticleSummary> MonthlyRanking { get; set; } = new();

    /// <summary>pixivision's own "Featured" sidebar widget. Identical on every page.</summary>
    public List<PixivisionArticleSummary> Featured { get; set; } = new();
}

/// <summary>A pixivision.net content category (e.g. Illustration, Manga, Interview) used for
/// filtering the article feed. <see cref="Slug"/> matches pixivision's own URL slug
/// (<c>/en/c/{slug}</c>).</summary>
public sealed record PixivisionCategory(string Slug, string Label);

/// <summary>A named, colored grouping of categories, mirroring pixivision's own nav bar
/// sections (Explore / Create / Discover). <see cref="Label"/> is null for the ungrouped
/// "All" entry.</summary>
public sealed record PixivisionCategoryGroup(string? Label, string? Color, IReadOnlyList<PixivisionCategory> Items)
{
    public bool HasLabel => !string.IsNullOrEmpty(Label);
}

/// <summary>Full detail of a single pixivision.net article, scraped from its article page.</summary>
public sealed class PixivisionArticleDetail
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? EyecatchUrl { get; set; }
    public List<PixivisionParagraph> Paragraphs { get; set; } = new();
    public List<PixivisionFeaturedWork> Works { get; set; } = new();

    /// <summary>pixivision's own "Newest articles tagged X" widget, shown at the bottom of the article.</summary>
    public List<PixivisionArticleSummary> RelatedLatest { get; set; } = new();

    /// <summary>The tag name pixivision keyed the "related articles" widgets off of (usually the
    /// article's primary/first tag), for display in the widget headings.</summary>
    public string? RelatedTagName { get; set; }

    /// <summary>pixivision's own "If you liked X, you will also love..." recommendation widget.</summary>
    public List<PixivisionArticleSummary> RelatedPopular { get; set; } = new();

    /// <summary>Interview-article ("Artist's Spotlight" etc.) section outline, scraped from the
    /// "Index" widget. Entry order matches the order of <see cref="PixivisionParagraphKind.Heading"/>
    /// blocks in <see cref="Paragraphs"/> 1:1, since pixivision renders both from the same list.</summary>
    public List<string> TableOfContents { get; set; } = new();

    /// <summary>Interviewee profile/bio card, only present on interview-style articles.</summary>
    public PixivisionProfile? Profile { get; set; }
}

/// <summary>Interviewee profile/bio card ("Artist's Spotlight" etc. articles) — avatar, name,
/// bio text and social links (pixiv, X, etc.).</summary>
public sealed class PixivisionProfile
{
    public string? AvatarUrl { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public List<PixivisionParagraphLink> Links { get; set; } = new();
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool HasLinks => Links.Count > 0;
}

/// <summary>Distinguishes the different article-body block types pixivision uses. Regular
/// articles only ever produce <see cref="Text"/>; interview-style articles ("Artist's
/// Spotlight" etc.) also use <see cref="Heading"/> (section titles), <see cref="Question"/>
/// (interviewer prompts) and <see cref="Answer"/> (interviewee replies, sometimes with an
/// avatar image). <see cref="Artwork"/> is a placeholder marking where an embedded Pixiv
/// artwork sits in the reading flow, for the optional "inline" artwork layout.</summary>
public enum PixivisionParagraphKind { Text, Heading, Question, Answer, Artwork }

/// <summary>One block of an article's body text, with any embedded hyperlinks pulled out
/// separately so they can still be opened even though the block itself renders as plain
/// wrapped text. See <see cref="Kind"/> for interview-article block types.</summary>
public sealed class PixivisionParagraph
{
    public string Text { get; set; } = string.Empty;
    public List<PixivisionParagraphLink> Links { get; set; } = new();
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasLinks => Links.Count > 0;
    public PixivisionParagraphKind Kind { get; set; } = PixivisionParagraphKind.Text;
    /// <summary>Interviewee avatar image, only set for <see cref="PixivisionParagraphKind.Answer"/> blocks.</summary>
    public string? AvatarUrl { get; set; }
    /// <summary>References the matching <see cref="PixivisionFeaturedWork.IllustId"/>, only set
    /// for <see cref="PixivisionParagraphKind.Artwork"/> blocks.</summary>
    public string? IllustId { get; set; }
    /// <summary>Some interview articles end with a heading (e.g. "Check out past Artist's
    /// Spotlight interviews!") immediately followed by a small grid of recommended article
    /// cards embedded directly in the body — set here on that <see cref="Heading"/> block so it
    /// can render together with its cards instead of as a dangling, empty section title.</summary>
    public List<PixivisionArticleSummary>? RelatedCards { get; set; }
    public bool HasRelatedCards => RelatedCards is { Count: > 0 };
    public bool IsHeading => Kind == PixivisionParagraphKind.Heading;
    public bool IsQuestion => Kind == PixivisionParagraphKind.Question;
    public bool IsAnswer => Kind == PixivisionParagraphKind.Answer;
    public bool IsArtwork => Kind == PixivisionParagraphKind.Artwork;
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
}

/// <summary>A hyperlink found inside an article paragraph's markup.</summary>
public sealed class PixivisionParagraphLink
{
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>A single Pixiv artwork embedded inside a pixivision article.</summary>
public sealed class PixivisionFeaturedWork
{
    public string IllustId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    /// <summary>pixivision's own caption text shown directly under the embedded artwork, if any.</summary>
    public string? Caption { get; set; }
    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);
}
