namespace DriveUnion.Core.Plans;

/// <summary>Which sentence an inbound Telegram document gets, when it gets one at all.</summary>
public enum InboundSizeVerdict
{
    /// <summary>Within both ceilings. The bridge fetches it.</summary>
    Accepted,

    /// <summary>
    /// Over the tenant's own per-file limit. <b>No path in the product accepts this file</b>, so the
    /// reply says that and carries no uploader link.
    /// </summary>
    OverPlan,

    /// <summary>
    /// Within the plan but over what the bot can carry. Telegram is the only thing refusing, so the
    /// reply points at the panel's chunked uploader, which takes 96 GB files — and that link is
    /// honest, which is the whole reason the two sentences are not one.
    /// </summary>
    OverTelegram,
}

/// <summary>
/// §4.2's ordering, as one function with no dependencies, so that both callers evaluate it the same
/// way and a test can put the two refusals side by side.
///
/// <para><b>Why the order is what it is.</b> Telegram inbound used to be bounded by
/// <c>api.telegram.org</c>'s 20 MB ceiling, which was below any plausible per-file tier, so the
/// plan's limit never fired there. Since the bot moved to a self-hosted Bot API server the inbound
/// ceiling is 2000 MB, and at an entry tier the plan's limit is now the <i>lower</i> of the two. That
/// inverts the conclusion: the check on the bridge is not a formality that runs for completeness, it
/// is the check that actually refuses, and its message is one customers will see routinely.</para>
///
/// <para><b>The order is chosen by which refusal is true regardless of route.</b> Over
/// <c>Tenant.MaxFileBytes</c>, nothing in the product accepts the file — that sentence is true
/// whichever way it arrives, and it carries no uploader link. Within <c>MaxFileBytes</c> but over
/// <c>Telegram:MaxReceiveBytes</c>, only Telegram is refusing, and its uploader link is real. Sending
/// a customer whose file no uploader will take to an uploader is the dead end the bot's own
/// no-dead-ends rule forbids, so the plan is evaluated first.</para>
///
/// <para><b>This chooses a sentence; it does not become the enforcement.</b> The declared
/// <c>document.file_size</c> is a claim from a third party. The authoritative check stays in
/// <c>IUploadCoordinator.BeginAsync</c>, against the reserve, backed by the abort on bytes actually
/// acknowledged. Two evaluations of one number with two different jobs: the early one picks a
/// sentence, the later one holds the line. Collapsing them into the early one would put the limit
/// back in a caller, which is the mistake this separation exists to prevent.</para>
/// </summary>
public static class InboundSizeRefusal
{
    /// <param name="declaredBytes">
    /// <c>document.file_size</c> from the update, or null when Telegram did not send one — an absent
    /// size is not a small file, so it is admitted here and refused later by the real check if it has
    /// to be.
    /// </param>
    /// <param name="maxFileBytes">The tenant's own per-file limit, from the row the resolver produced.</param>
    /// <param name="telegramCeilingBytes">
    /// <c>Telegram:MaxReceiveBytes</c>. Configuration, never a constant: development talks to the
    /// cloud API at 20 MB and production to our own server at 2000 MB.
    /// </param>
    public static InboundSizeVerdict Evaluate(long? declaredBytes, long maxFileBytes, long telegramCeilingBytes)
    {
        if (declaredBytes is not { } declared) return InboundSizeVerdict.Accepted;

        // The plan first, deliberately. See the type's remarks.
        if (declared > maxFileBytes) return InboundSizeVerdict.OverPlan;

        // The existing bridge treats the ceiling as exclusive — a file at exactly the ceiling is
        // refused — and this must not quietly disagree with it, or one number would mean two things.
        return declared >= telegramCeilingBytes
            ? InboundSizeVerdict.OverTelegram
            : InboundSizeVerdict.Accepted;
    }
}

/// <summary>
/// What a plan refusal says, where the panel's two-language catalogue cannot reach.
///
/// <para>The bot has no request culture and no <c>UiText</c>: every string it sends is a Persian
/// constant beside the other bot strings. This one lives here rather than in
/// <c>TelegramMessages</c> only because P1 does not own that file; it is written to sit beside them
/// and to be moved there when the bridge is wired.</para>
/// </summary>
public static class PlanRefusalMessages
{
    /// <summary>
    /// The refusal that means "nothing here will take this file".
    ///
    /// <para>It carries <b>no URL</b>, and that absence is the whole point — see
    /// <see cref="InboundSizeVerdict.OverPlan"/>. It still ends somewhere, per the bot's
    /// no-dead-ends rule: the next action is a smaller file or a bigger plan, and the plan is
    /// changed by the operator rather than by a checkout that does not exist.</para>
    ///
    /// <para>It names no Google account, no account count and no pool.</para>
    /// </summary>
    public static string InboundOverPlan(string limit) =>
        $"پلن شما فایلی بزرگ‌تر از {limit} را نمی‌پذیرد.\n"
        + "این فایل از هیچ راهی — نه تلگرام و نه بارگذارِ پنل — ذخیره نمی‌شود. "
        + "فایل کوچک‌تری بفرستید یا برای بالا بردن سقف پلن با ما تماس بگیرید.";
}
