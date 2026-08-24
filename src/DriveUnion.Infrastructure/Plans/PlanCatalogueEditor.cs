using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// <b>The only writer of the plan catalogue.</b>
///
/// <para>Everything here writes <c>Plans</c> and nothing here writes a tenant's four columns. That
/// is not a convention — <c>PlanSourceRulesTests</c> reads this source and fails if it ever stops
/// being true, because a tenant's ceiling with no <c>TenantQuotaChange</c> row behind it is a
/// support conversation with no answer.</para>
///
/// <para><see cref="ReapplyAsync"/> is the single exception, and it is an exception in name only:
/// it reaches workspaces by calling <see cref="ITenantPlanService.SetTenantPlanAsync"/> once per
/// workspace, so the one writer stays the one writer and every ceiling it moves is audited.</para>
/// </summary>
public sealed class PlanCatalogueEditor(
    DriveUnionDbContext db,
    ITenantPlanService tenantPlans,
    TimeProvider clock,
    IOptions<PlansOptions> options) : IPlanCatalogueEditor
{
    /// <summary>
    /// The step between neighbours after a reorder.
    ///
    /// <para>The list is renumbered on every move rather than nudged, so a catalogue that arrived
    /// with duplicate or exhausted sort orders — a seed edited by hand, two rows created in the same
    /// second — comes out of one move ordered. Ten leaves room for a manual insert between two rows,
    /// which is what the seeded 10/20/30 was already doing.</para>
    /// </summary>
    private const int SortStep = 10;

    /// <summary>The <c>Plan.Name</c> column's width.</summary>
    private const int NameLimit = 120;

    /// <summary>The <c>Plan.Code</c> column's width, and the shortest code worth typing.</summary>
    private const int CodeLimit = 32;

    private const int CodeMinimum = 2;

    public async Task<PlanCatalogueState> StateAsync(CancellationToken cancellationToken)
    {
        var rows = await Ordered(db.Plans.AsNoTracking()).ToListAsync(cancellationToken);

        // One projection over every workspace rather than two counts per tier: the operator's own
        // overview screen already reads every tenant row, and this list is three tiers long.
        var carried = await CarriedAsync(planId: null, cancellationToken);

        var defaultCode = options.Value.DefaultPlanCode;

        return new PlanCatalogueState(
            [.. rows.Select(row => Describe(row, carried, defaultCode))],
            defaultCode,

            // The half of Plans:DefaultPlanCode that can only be checked against the database, and
            // therefore the half start-up deliberately does not check. A setting naming no row makes
            // every sign-up throw KeyNotFoundException; the screen says it in words instead.
            rows.Exists(r => string.Equals(r.Code, defaultCode, StringComparison.Ordinal)));
    }

    public async Task<PlanUsage?> UsageAsync(string planCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);

        var row = await db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == planCode, cancellationToken);

        return row is null
            ? null
            : Describe(row, await CarriedAsync(row.Id, cancellationToken), options.Value.DefaultPlanCode);
    }

    public async Task<PlanSummary> CreateAsync(PlanDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var code = NormaliseCode(draft.Code);
        var name = NormaliseName(draft.Name);
        Check(draft.Numbers);

        await RefuseDuplicateAsync(code, existingId: null, cancellationToken);

        // Appended rather than inserted at a chosen position: where a new tier sits is the arrow
        // buttons' business, and a sort-order box on a create form is a number an operator has to
        // guess at against rows they cannot see from there.
        var last = await db.Plans.MaxAsync(p => (int?)p.SortOrder, cancellationToken) ?? 0;

        var row = new Plan
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = name,
            IsRetired = false,
            SortOrder = last + SortStep,
            CreatedAt = clock.GetUtcNow(),
        };

        row.SetNumbers(draft.Numbers);

        db.Plans.Add(row);
        await SaveRefusingDuplicateAsync(cancellationToken);

        return Summarise(row);
    }

    public async Task<PlanSummary> EditAsync(
        string planCode,
        PlanDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var row = await RequireAsync(planCode, cancellationToken);

        var code = NormaliseCode(draft.Code);
        var name = NormaliseName(draft.Name);
        Check(draft.Numbers);

        // Plans:DefaultPlanCode is a string in a configuration file and nothing reconciles the two.
        // Re-coding the row it names leaves the setting pointing at nothing, TenantPlanService
        // throwing KeyNotFoundException, and the first person to find out being a customer whose
        // sign-up answers 500.
        if (IsConfiguredDefault(row.Code) && !string.Equals(code, row.Code, StringComparison.Ordinal))
        {
            throw Refuse(
                PlanEditRefusal.DefaultCannotBeRecoded,
                $"'{row.Code}' is Plans:DefaultPlanCode and cannot be re-coded to '{code}'.");
        }

        await RefuseDuplicateAsync(code, row.Id, cancellationToken);

        row.Code = code;
        row.Name = name;

        // The whole of the edit. Every workspace on this tier keeps the numbers it was given —
        // nothing on any enforcement path joins back to this row, and ReapplyAsync is the only
        // thing in the product that reaches them.
        row.SetNumbers(draft.Numbers);

        await SaveRefusingDuplicateAsync(cancellationToken);

        return Summarise(row);
    }

    public async Task<PlanSummary> SetRetiredAsync(
        string planCode,
        bool retired,
        CancellationToken cancellationToken)
    {
        var row = await RequireAsync(planCode, cancellationToken);

        if (retired && IsConfiguredDefault(row.Code))
        {
            throw Refuse(
                PlanEditRefusal.DefaultCannotBeRetired,
                $"'{row.Code}' is Plans:DefaultPlanCode; retiring it would refuse every new workspace.");
        }

        // Deliberately allowed for a tier workspaces are on. That is what retirement is for: it
        // hides the tier from new assignment and leaves everybody on it working, because their
        // numbers are on their own row.
        row.IsRetired = retired;

        await db.SaveChangesAsync(cancellationToken);

        return Summarise(row);
    }

    public async Task<PlanSummary> MoveAsync(
        string planCode,
        PlanMove direction,
        CancellationToken cancellationToken)
    {
        var row = await RequireAsync(planCode, cancellationToken);

        var ordered = await Ordered(db.Plans).ToListAsync(cancellationToken);

        var index = ordered.FindIndex(p => p.Id == row.Id);
        var target = direction == PlanMove.Up ? index - 1 : index + 1;

        // A no-op at either end. An arrow that refuses at the edge of a list is a refusal about
        // nothing, and the screen does not draw the arrow there anyway.
        if (target < 0 || target >= ordered.Count) return Summarise(row);

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = (i + 1) * SortStep;

        await db.SaveChangesAsync(cancellationToken);

        return Summarise(row);
    }

    public async Task DeleteAsync(string planCode, CancellationToken cancellationToken)
    {
        var row = await RequireAsync(planCode, cancellationToken);

        if (IsConfiguredDefault(row.Code))
        {
            throw Refuse(
                PlanEditRefusal.DefaultCannotBeDeleted,
                $"'{row.Code}' is Plans:DefaultPlanCode; deleting it would refuse every new workspace.");
        }

        // Tenant.PlanId is a Restrict foreign key, so the database refuses this too — as a
        // constraint violation, which reaches a screen as a 500 naming an index. Asked here first so
        // the answer is a sentence, and so the sentence can name retirement as the way out.
        if (await db.Tenants.AnyAsync(t => t.PlanId == row.Id, cancellationToken))
        {
            throw Refuse(
                PlanEditRefusal.InUseCannotBeDeleted,
                $"'{row.Code}' is a workspace's tier; retire it instead.");
        }

        db.Plans.Remove(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The check above raced a sign-up. The foreign key is what makes the race safe; this
            // turns its exception back into the same sentence.
            throw Refuse(
                PlanEditRefusal.InUseCannotBeDeleted,
                $"'{row.Code}' became a workspace's tier while it was being deleted.");
        }
    }

    public async Task<int> ReapplyAsync(
        string planCode,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var row = await RequireAsync(planCode, cancellationToken);

        var affected = await CarriedAsync(row.Id, cancellationToken);
        var target = row.Numbers;

        // Counted before the sweep, because afterwards every one of them matches. This is the figure
        // the confirmation screen showed the operator, and it is the honest answer to "how many
        // customers did that move" — the rest were already on these numbers and are written to
        // without producing a history row.
        var moving = affected.Count(t => t.Limits != target);

        // One transaction over the whole sweep. "Re-apply this tier" is one act to the operator who
        // pressed it, and half a bulk move — some customers on the new numbers, some on the old, and
        // a history that stops mid-list — is worse than none of it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var tenant in affected)
        {
            // Through the one writer, which is what puts a TenantQuotaChange row behind every number
            // that moves. A loop assigning the four columns here would be faster and would be exactly
            // the silent bulk move the history table exists to answer for.
            await tenantPlans.SetTenantPlanAsync(
                tenant.Key, row.Code, reason, changedByUserId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return moving;
    }

    private static IQueryable<Plan> Ordered(IQueryable<Plan> plans) =>
        plans.OrderBy(p => p.SortOrder).ThenBy(p => p.Code);

    /// <summary>
    /// What each workspace is actually holding, keyed by the tier it is labelled with — one tier's
    /// workspaces, or every one of them when <paramref name="planId"/> is null.
    ///
    /// <para>Projected into an anonymous row and paired up here rather than constructed in the
    /// query: the shape a screen wants is a value it can compare in one <c>!=</c>, and that is not
    /// a shape a provider has to be asked to build.</para>
    /// </summary>
    private async Task<List<CarriedLimits>> CarriedAsync(Guid? planId, CancellationToken cancellationToken)
    {
        var query = db.Tenants.AsNoTracking().Where(t => t.PlanId != null);

        if (planId is { } id) query = query.Where(t => t.PlanId == id);

        var rows = await query
            .Select(t => new
            {
                t.Id,
                Plan = t.PlanId!.Value,
                t.StorageQuotaBytes,
                t.MaxFileBytes,
                t.MonthlyEgressBytes,
                t.MaxMembers,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => new CarriedLimits(
                r.Id,
                r.Plan,
                new PlanNumbers(r.StorageQuotaBytes, r.MaxFileBytes, r.MonthlyEgressBytes, r.MaxMembers))),
        ];
    }

    private async Task<Plan> RequireAsync(string planCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);

        return await db.Plans.FirstOrDefaultAsync(p => p.Code == planCode, cancellationToken)
            ?? throw Refuse(PlanEditRefusal.NotFound, $"No plan is coded '{planCode}'.");
    }

    private bool IsConfiguredDefault(string code) =>
        string.Equals(code, options.Value.DefaultPlanCode, StringComparison.Ordinal);

    /// <summary>
    /// A code an operator can put in a configuration file without surprises: lower-case ASCII,
    /// digits and hyphens, starting with a letter.
    ///
    /// <para>The width is the column's. The shape is <c>Plans:DefaultPlanCode</c>'s — that setting is
    /// compared with <c>==</c>, so a code carrying a capital or a trailing space is one an operator
    /// transcribes subtly wrong and only discovers at somebody's sign-up. Trimming and lower-casing
    /// on the way in also makes "Starter" and "starter" the same code rather than two rows the
    /// unique index would happily hold.</para>
    /// </summary>
    private static string NormaliseCode(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim().ToLowerInvariant();

        var wellFormed =
            trimmed.Length is >= CodeMinimum and <= CodeLimit
            && char.IsAsciiLetterLower(trimmed[0])
            && trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

        return wellFormed
            ? trimmed
            : throw Refuse(PlanEditRefusal.CodeMalformed, $"'{code}' is not a plan code.");
    }

    private static string NormaliseName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        return trimmed.Length is > 0 and <= NameLimit
            ? trimmed
            : throw Refuse(
                PlanEditRefusal.NameInvalid,
                $"A tier's name is required and is at most {NameLimit} characters.");
    }

    private static void Check(PlanNumbers numbers)
    {
        long[] ceilings = [numbers.StorageBytes, numbers.MaxFileBytes, numbers.MonthlyEgressBytes];

        foreach (var bytes in ceilings)
        {
            // Whole gigabytes as well as in range: the form's unit is the guarantee that a number
            // typed here comes back out of the form unchanged, and a byte figure that is not a whole
            // number of them would be rounded on the next save by whoever opened the screen next.
            if (!PlanSize.IsWholeGigabytes(bytes) || !PlanSize.IsInRange(PlanSize.ToGigabytes(bytes)))
            {
                throw Refuse(
                    PlanEditRefusal.NumberOutOfRange,
                    $"{bytes} is not a ceiling this catalogue can hold.");
            }
        }

        if (numbers.MaxMembers < 1)
        {
            throw Refuse(
                PlanEditRefusal.NumberOutOfRange,
                $"A tier of {numbers.MaxMembers} seats has nobody on it.");
        }

        // A per-file ceiling above the storage cap can never be reached — the storage check refuses
        // the upload first. It reads as generosity on the customer's card and is a promise the tier
        // cannot keep.
        if (numbers.MaxFileBytes > numbers.StorageBytes)
        {
            throw Refuse(
                PlanEditRefusal.FileLargerThanStorage,
                $"A per-file ceiling of {numbers.MaxFileBytes} does not fit in {numbers.StorageBytes}.");
        }
    }

    private async Task RefuseDuplicateAsync(
        string code,
        Guid? existingId,
        CancellationToken cancellationToken)
    {
        var query = db.Plans.AsNoTracking().Where(p => p.Code == code);

        if (existingId is { } id) query = query.Where(p => p.Id != id);

        if (await query.AnyAsync(cancellationToken))
        {
            throw Refuse(PlanEditRefusal.CodeTaken, $"Another tier is already coded '{code}'.");
        }
    }

    /// <summary>
    /// The unique index, translated. The check above loses the race with a second operator saving
    /// the same code in the same second, and the index is what actually holds the rule — so its
    /// exception has to arrive as the same sentence rather than as a 500 naming an index.
    /// </summary>
    private async Task SaveRefusingDuplicateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw Refuse(PlanEditRefusal.CodeTaken, "Another tier took this code first.");
        }
    }

    private static PlanEditRefusedException Refuse(PlanEditRefusal reason, string message) =>
        new(reason, message);

    private static PlanSummary Summarise(Plan plan) =>
        new(plan.Id, plan.Code, plan.Name, plan.Numbers, plan.IsRetired, plan.SortOrder);

    private PlanUsage Describe(Plan row, List<CarriedLimits> carried, string defaultCode)
    {
        var mine = carried.FindAll(c => c.PlanId == row.Id);

        return new PlanUsage(
            Summarise(row),
            mine.Count,
            mine.Count(c => c.Limits != row.Numbers),
            string.Equals(row.Code, defaultCode, StringComparison.Ordinal));
    }

    /// <summary>
    /// What one workspace currently holds, beside the tier it is labelled with.
    ///
    /// <para>The limits travel as one <see cref="PlanNumbers"/> so the question this screen asks —
    /// has this customer drifted from the tier they are on — is one comparison rather than four.</para>
    /// </summary>
    private readonly record struct CarriedLimits(Guid Key, Guid PlanId, PlanNumbers Limits);
}
