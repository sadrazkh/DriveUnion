namespace DriveUnion.Core.Sharing;

/// <summary>
/// What the reporter says is wrong with a file. Categories rather than free text alone, because an
/// operator triaging twenty reports needs to sort them before reading them.
/// </summary>
public enum AbuseKind
{
    /// <summary>Anything that does not fit below. The note is the whole of it.</summary>
    Other = 0,

    /// <summary>Somebody else's work, published without permission. The commonest by far.</summary>
    Copyright = 1,

    /// <summary>Malware, a phishing kit, a credential harvester.</summary>
    Malware = 2,

    /// <summary>Content that is illegal to host anywhere, whatever the operator's policy says.</summary>
    Illegal = 3,

    /// <summary>Somebody's private information published without their consent.</summary>
    Privacy = 4,
}

public enum AbuseReportStatus
{
    /// <summary>Nobody has looked yet.</summary>
    Open = 0,

    /// <summary>Looked at, and the link was taken down.</summary>
    Upheld = 1,

    /// <summary>Looked at, and there was nothing wrong. The link is untouched.</summary>
    Rejected = 2,
}

/// <summary>
/// A complaint about a public link, and the reason this is not a «nice to have».
///
/// <para><b>The failure it prevents is not legal, it is operational.</b> Every customer's files sit
/// in a Google account the operator owns. A file somebody reports to Google — a phishing kit, a
/// pirated film — gets that <i>account</i> suspended, and that account holds the files of every
/// workspace routed onto it. So one customer's bad file takes down dozens of innocent ones, and the
/// only thing that stops it is the operator hearing about the file before Google does and removing
/// it first.</para>
///
/// <para>Which is what the address on the public page is for — and until now that address was the
/// literal string <c>abuse@yourdomain.com</c>, printed on every public page in the product. A report
/// route that nobody reads is worse than none: it looks like diligence and delivers nothing.</para>
///
/// <para><b>Anonymous on purpose.</b> The person reporting is not a customer and will not make an
/// account to tell you your service is hosting their film. The cost of that is a form anybody can
/// submit, which is what the rate limit and the per-link cap below are for.</para>
/// </summary>
public sealed class AbuseReport
{
    public Guid Id { get; set; }

    /// <summary>
    /// The link complained about.
    ///
    /// <para>The link and not the file, because the slug is all the reporter ever saw and it is what
    /// they can quote back. The file behind it is one join away and may be shared by several links —
    /// the operator wants to know about all of them, which is a question this row can answer and a
    /// file id could not have asked.</para>
    /// </summary>
    public Guid ShareLinkId { get; set; }

    /// <summary>
    /// Whose workspace it is, copied at the moment of the report.
    ///
    /// <para>Denormalised deliberately: «how many reports has this workspace had» is the question
    /// that decides whether one file is a mistake or the account is the problem, and it has to keep
    /// working after the link is revoked and the file is deleted — which is exactly when somebody
    /// asks it.</para>
    /// </summary>
    public Guid TenantId { get; set; }

    public AbuseKind Kind { get; set; }

    /// <summary>What they wrote. Shown to the operator and to nobody else.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Where to write back, if they offered one. Optional, and never required.
    ///
    /// <para>Demanding an address turns a thirty-second report into a decision about giving a
    /// stranger your email, and the report is worth more than the reply.</para>
    /// </summary>
    public string? ReporterEmail { get; set; }

    /// <summary>
    /// The reporter's address, hashed the same way a download's is.
    ///
    /// <para>Not for contacting anybody — for telling one person filing forty reports apart from
    /// forty people filing one, which is the difference between a queue and a denial of service on
    /// the operator's attention.</para>
    /// </summary>
    public string? ReporterIpHash { get; set; }

    public AbuseReportStatus Status { get; set; }

    /// <summary>What the operator decided, for the next person who reads the queue.</summary>
    public string? Resolution { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public const int MaxNoteLength = 2000;

    public const int MaxEmailLength = 256;

    public const int MaxResolutionLength = 512;

    /// <summary>
    /// How many open reports one link may collect.
    ///
    /// <para>Past this the form thanks the reporter and writes nothing. Ten identical complaints
    /// about one file tell the operator exactly what one does, and the queue is the operator's
    /// attention — the thing an abusive reporter would be attacking if they wanted the real
    /// complaints buried.</para>
    /// </summary>
    public const int MostOpenPerLink = 10;
}
