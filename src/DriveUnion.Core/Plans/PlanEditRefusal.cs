namespace DriveUnion.Core.Plans;

/// <summary>
/// Why the catalogue refused an edit.
///
/// <para>A reason rather than a message, because the sentence a person reads is bilingual and lives
/// in <c>UiText.PlanAdmin</c>. A service that carried the wording would have written half the panel
/// in one language and put it somewhere <c>LocalizationCatalogueTests</c> cannot see.</para>
///
/// <para>Not persisted, so the values are ordinary. Nothing writes one to a column.</para>
/// </summary>
public enum PlanEditRefusal
{
    /// <summary>No tier is coded that.</summary>
    NotFound,

    /// <summary>
    /// Not a code this product would mint. A code is what <c>Plans:DefaultPlanCode</c> and a
    /// deployment's configuration name a tier by, so it is lower-case ASCII with hyphens and no
    /// spaces — a code with a capital or a space in it is one an operator will mistype into a
    /// setting and never see fail until a customer signs up.
    /// </summary>
    CodeMalformed,

    /// <summary>
    /// Another tier already holds this code. Refused here with a sentence rather than left to the
    /// unique index, whose <c>DbUpdateException</c> reaches a screen as a 500 naming an index.
    /// </summary>
    CodeTaken,

    /// <summary>No name, or one longer than the column holds. A tier with no name is a row a
    /// customer's card would render blank.</summary>
    NameInvalid,

    /// <summary>
    /// A ceiling below one gigabyte, above <see cref="PlanSize.MaxGigabytes"/>, or a seat count
    /// below one. Zero is the dangerous one: it refuses every upload on the tier.
    /// </summary>
    NumberOutOfRange,

    /// <summary>
    /// A per-file ceiling above the storage cap. Nothing could ever be uploaded at that size — the
    /// storage check would refuse it first — so the number is a promise the tier cannot keep.
    /// </summary>
    FileLargerThanStorage,

    /// <summary>
    /// The tier <c>Plans:DefaultPlanCode</c> names cannot be re-coded. The setting would go on
    /// naming a code no row holds, and the first symptom would be a 500 at somebody's sign-up.
    /// </summary>
    DefaultCannotBeRecoded,

    /// <summary>The default cannot be retired: every new workspace is created on it.</summary>
    DefaultCannotBeRetired,

    /// <summary>The default cannot be deleted, for the same reason it cannot be retired.</summary>
    DefaultCannotBeDeleted,

    /// <summary>
    /// A tier a workspace is on. <c>Tenant.PlanId</c> is a <c>Restrict</c> foreign key, so the
    /// database would refuse this anyway — and would do it as a constraint violation on a screen.
    /// Retirement is how a tier goes away.
    /// </summary>
    InUseCannotBeDeleted,
}

/// <summary>
/// The catalogue said no, for a reason a screen can put into a sentence.
///
/// <para>An exception rather than a result type because every command on
/// <c>IPlanCatalogueEditor</c> returns something a caller wants, and a refusal is not one of the
/// things it wants — the same argument <c>PlanLimitExceededException</c> makes. It derives from
/// <see cref="InvalidOperationException"/> so that a caller which already treats an invalid
/// operation as an operator's mistake rather than a fault keeps doing so.</para>
///
/// <para><see cref="Exception.Message"/> is for a log and is not shown to anybody:
/// <see cref="Reason"/> is what the screen maps to a bilingual sentence.</para>
/// </summary>
public sealed class PlanEditRefusedException(PlanEditRefusal reason, string message)
    : InvalidOperationException(message)
{
    public PlanEditRefusal Reason { get; } = reason;
}
