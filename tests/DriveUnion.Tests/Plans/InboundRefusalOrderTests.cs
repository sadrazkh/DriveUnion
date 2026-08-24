using DriveUnion.Core.Plans;
using DriveUnion.Core.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// One inbound file, two possible refusals, and the order between them.
///
/// <para>The bug guarded against is two branches converging on one sentence. «برای تلگرام بزرگ است» and
/// «پلن شما فایلی به این اندازه را نمی‌پذیرد» are different statements with different next actions: the
/// first points at the panel's chunked uploader, which carries 96 GB files; the second means nothing in
/// the product will accept this file, and sending that customer to an uploader that refuses them again
/// is a dead end.</para>
///
/// <para>Asserted on the raw outbound strings rather than on an enum alone, because a sentence is what
/// the customer receives and an enum is not.</para>
/// </summary>
public class InboundRefusalOrderTests
{
    /// <summary>What production's self-hosted Bot API server accepts. 20 MB was the cloud API's.</summary>
    private const long TelegramCeiling = 2_000_000_000;

    private const long TenantMaxFile = 1_073_741_824;

    [Fact]
    public void Over_both_ceilings_produces_the_plan_refusal()
    {
        // 2.5 GB: past the tenant's 1 GiB and past the bot's 2000 MB, so both refusals are true.
        var verdict = InboundSizeRefusal.Evaluate(
            declaredBytes: 2_500_000_000, TenantMaxFile, TelegramCeiling);

        // Over Tenant.MaxFileBytes, no path accepts the file. That is true whichever route it arrived
        // by, so it is the sentence that wins — even though the Telegram ceiling is also in play.
        verdict.Should().Be(InboundSizeVerdict.OverPlan);
    }

    [Fact]
    public void Over_only_the_telegram_ceiling_produces_the_telegram_refusal()
    {
        // The same file, for a tenant whose tier takes 8 GiB. Now only the bot is refusing, and its
        // message's uploader link is honest.
        var verdict = InboundSizeRefusal.Evaluate(
            declaredBytes: 2_500_000_000, maxFileBytes: 8L * 1024 * 1024 * 1024, TelegramCeiling);

        verdict.Should().Be(InboundSizeVerdict.OverTelegram);
    }

    [Fact]
    public void Under_the_bots_ceiling_but_over_the_plan_is_still_the_plan_refusal()
    {
        // The case the design says is now routine: at an entry tier the plan's limit is the lower of
        // the two, so the check on the bridge is not a formality — it is the one that refuses.
        InboundSizeRefusal.Evaluate(1_500_000_000, TenantMaxFile, TelegramCeiling)
            .Should().Be(InboundSizeVerdict.OverPlan);
    }

    [Fact]
    public void Within_both_is_accepted()
    {
        InboundSizeRefusal.Evaluate(10_000_000, TenantMaxFile, TelegramCeiling)
            .Should().Be(InboundSizeVerdict.Accepted);
    }

    [Fact]
    public void A_missing_declared_size_is_not_a_small_file_and_is_not_refused_here()
    {
        // Telegram does not always send file_size. Treating its absence as zero would admit a file
        // that the real check then has to refuse, which is correct — this evaluation picks a
        // sentence, it does not become the enforcement.
        InboundSizeRefusal.Evaluate(null, TenantMaxFile, TelegramCeiling)
            .Should().Be(InboundSizeVerdict.Accepted);
    }

    [Fact]
    public void The_two_refusals_are_different_sentences_and_only_one_offers_an_uploader()
    {
        const string uploader = "https://panel.example.test/files/upload";

        var telegramRefusal = TelegramMessages.InboundTooLarge("2000 MB", uploader);
        var planRefusal = PlanRefusalMessages.InboundOverPlan("1 GB");

        planRefusal.Should().NotBe(telegramRefusal);

        // The honest link, on the refusal where it is honest: within MaxFileBytes but over the bot's
        // ceiling, the panel's uploader really will take this file.
        telegramRefusal.Should().Contain(uploader);

        // And no link at all on the one where it would be a dead end. Asserted on the raw string
        // because the bug being guarded against is two branches converging on one sentence — an enum
        // comparison would not see that.
        planRefusal.Should().NotContain("http");
        planRefusal.Should().NotContain(uploader);

        // Every refusal still ends somewhere, and none of them names a Google account, an account
        // count or the pool.
        planRefusal.Should().NotBeNullOrWhiteSpace();
        planRefusal.Should().NotContain("Google");
        planRefusal.Should().NotContain("گوگل");
        planRefusal.Should().Contain("1 GB", "a refusal that does not name the limit is a support ticket");
    }

    [Fact]
    public void The_plan_refusal_is_not_the_temporarily_unavailable_sentence()
    {
        var planRefusal = PlanRefusalMessages.InboundOverPlan("1 GB");

        // «آپلود موقتاً در دسترس نیست» belongs to a full pool, and it promises that waiting will help.
        // Waiting does nothing to a file that is too big, and a customer who retries for an hour on
        // that advice is a support ticket that the wording caused.
        planRefusal.Should().NotBe(TelegramMessages.UploadUnavailable);
        planRefusal.Should().NotContain("موقتاً");
    }
}
