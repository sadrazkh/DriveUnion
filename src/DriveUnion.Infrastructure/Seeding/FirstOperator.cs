using DriveUnion.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Seeding;

/// <summary>What asking for the first operator did.</summary>
public enum FirstOperatorOutcome
{
    /// <summary>The row is in the database and is the account to sign in.</summary>
    Created,

    /// <summary>
    /// Somebody else already has the job — either a configured seed, an earlier visit to the setup
    /// screen, or another request that was a few milliseconds quicker. Nothing was written.
    /// </summary>
    AlreadyProvisioned,

    /// <summary>
    /// Identity would not have it: the password fails the policy, or the address is malformed or
    /// taken. <see cref="FirstOperatorResult.Errors"/> says which, in Identity's own words.
    /// </summary>
    Refused,
}

/// <param name="User">The created account, and null for every other outcome.</param>
/// <param name="Errors">
/// Identity's own refusals. Empty unless <see cref="FirstOperatorOutcome.Refused"/>; never contains
/// the password, only what is wrong with it.
/// </param>
public sealed record FirstOperatorResult(
    FirstOperatorOutcome Outcome,
    AppUser? User,
    IReadOnlyList<IdentityError> Errors);

/// <summary>
/// The second door into an empty database, beside <see cref="IdentitySeeder"/>'s configured one.
///
/// The seeder needs <c>DriveUnion:Seed:OperatorPassword</c> to exist before the process starts; this
/// is what the panel calls when it does not, so the first screen can create the account instead of
/// asking for a command line. The two are independent: an operator seeded from configuration closes
/// this door, and this door closes nothing the seeder does.
///
/// ── What stops a second operator being created ──────────────────────────────────────────────────
///
/// Two things, and only the second one survives a race.
///
/// 1. <see cref="ExistsAsync"/> is asked on every request to the setup route — the GET that renders
///    the form and, separately, the POST that writes. It is a database read, not a flag captured at
///    boot and not a check in a view: a direct POST from a saved page, from curl, or from a second
///    tab is judged by the same query as the first one. This closes the door for everything except
///    two requests overlapping inside the window between the read and the insert.
///
/// 2. <see cref="SlotId"/> closes that window. Every operator this class creates is given the same
///    fixed primary key, so the second INSERT is not a second row — it is a duplicate of the first
///    one's key, and the database refuses it. Two requests that both read "no operator" at the same
///    instant therefore both attempt the insert and exactly one commits; the loser's
///    <c>SaveChanges</c> raises a unique-violation, which is caught below and reported as
///    <see cref="FirstOperatorOutcome.AlreadyProvisioned"/> so the caller answers "this route is
///    gone" rather than a 500. The arbiter is the primary-key index that
///    <c>IdentityDbContext</c> already declares — no migration, no extra table, and it holds across
///    processes, which an in-process lock would not: the panel behind a load balancer is two
///    processes reading one Postgres.
///
/// A same-address race is decided one step earlier, by Identity's unique index on the normalised
/// user name, and surfaces as a duplicate error rather than an exception; that path is folded into
/// the same outcome below so both losers are told the same thing.
///
/// Nothing here is a static method for style: taking <see cref="UserManager{TUser}"/> as an argument
/// means the setup screen needs no service registered beyond the ones <c>AddIdentity</c> already
/// provides, so adding this door costs Program.cs nothing.
/// </summary>
public static class FirstOperator
{
    /// <summary>
    /// The primary key of the operator this class creates — a fixed constant, chosen once and not
    /// derived from anything, so that two concurrent first-run requests collide on it.
    ///
    /// It identifies the <em>slot</em>, not a person: whoever fills it picks their own address, and
    /// an operator seeded from configuration has an ordinary random id and never touches this one.
    /// </summary>
    public static readonly Guid SlotId = new("9a6bd0f4-4e33-4a5e-9c1c-1b7f0d2ea401");

    /// <summary>
    /// Whether the panel already has an operator — any operator, however it got there. This is the
    /// question the setup route is gated on, so a configured seed and an earlier setup both close it.
    /// </summary>
    public static async Task<bool> ExistsAsync(
        UserManager<AppUser> users,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);

        return await users.Users.AnyAsync(u => u.IsOperator, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the first operator, or explains why it did not. Writes nothing on any path but
    /// <see cref="FirstOperatorOutcome.Created"/>.
    /// </summary>
    /// <param name="password">
    /// Passed to Identity and nowhere else. It is not logged, not echoed, and not kept after this
    /// call — <see cref="FirstOperatorResult"/> carries the account, never the credential.
    /// </param>
    public static async Task<FirstOperatorResult> CreateAsync(
        UserManager<AppUser> users,
        string email,
        string password,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (await ExistsAsync(users, cancellationToken).ConfigureAwait(false))
        {
            return Taken;
        }

        var user = new AppUser
        {
            Id = SlotId,
            UserName = email,
            Email = email,
            // There is no mail sender in M1 and nobody to send to: this is the account that exists so
            // the panel can be opened for the first time. Same reasoning as IdentitySeeder's.
            EmailConfirmed = true,
            IsOperator = true,
            TenantId = null,
            CreatedAt = createdAt,
        };

        IdentityResult result;

        try
        {
            result = await users.CreateAsync(user, password).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The insert reached the database and the database refused it. If the slot is filled,
            // this is the losing half of a race and the product is in exactly the state the caller
            // wanted; anything else is a real write failure and must not be dressed up as success.
            if (await SlotIsFilledAsync(users, cancellationToken).ConfigureAwait(false)
                || await ExistsAsync(users, cancellationToken).ConfigureAwait(false))
            {
                return Taken;
            }

            throw;
        }

        if (result.Succeeded)
        {
            return new FirstOperatorResult(FirstOperatorOutcome.Created, user, []);
        }

        // Two requests that raced with the *same* address never reach the primary key: Identity's own
        // validator finds the winner's row first and answers with a duplicate. Told apart from a
        // genuinely taken address only by whether an operator now exists — and if one does, "this
        // route is gone" is the truthful answer, not "that user name is taken".
        var duplicated = result.Errors.Any(e =>
            e.Code == nameof(IdentityErrorDescriber.DuplicateUserName)
            || e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));

        if (duplicated && await ExistsAsync(users, cancellationToken).ConfigureAwait(false))
        {
            return Taken;
        }

        return new FirstOperatorResult(FirstOperatorOutcome.Refused, null, [.. result.Errors]);
    }

    private static FirstOperatorResult Taken =>
        new(FirstOperatorOutcome.AlreadyProvisioned, null, []);

    private static async Task<bool> SlotIsFilledAsync(
        UserManager<AppUser> users,
        CancellationToken cancellationToken) =>
        await users.Users.AnyAsync(u => u.Id == SlotId, cancellationToken).ConfigureAwait(false);
}
