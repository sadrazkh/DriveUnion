namespace DriveUnion.Tests.Fakes;

/// <summary>
/// A clock a test can move.
///
/// Expiry is a rule this product is judged on — a link that dies a second early or a second late is
/// a support ticket — so the tests set the time rather than sleeping through it.
/// </summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
