using DriveUnion.Core.Application;

namespace DriveUnion.Tests.Fakes;

/// <summary>
/// An <see cref="IEgressAllowance"/> that answers the same way whoever asks, and counts the asking.
///
/// <para>The interesting question about the gate on the download path is «was it consulted, and what
/// did the route do with the answer» — which is a question about the controller. What the two numbers
/// are and where they come from is <c>EgressAllowanceReaderTests</c>' business, over a real database;
/// a double that read one would be a second implementation of the thing under test.</para>
///
/// <para><see cref="WithRoom"/> is the default a test asks for when it is about something else
/// entirely: a probe, a range, a disposition. Without it every one of those would have to know that
/// a traffic gate exists.</para>
/// </summary>
public sealed class FixedEgressAllowance(EgressStanding standing) : IEgressAllowance
{
    /// <summary>A workspace well inside its allowance, for a test that is about something else.</summary>
    public static FixedEgressAllowance WithRoom() =>
        new(new EgressStanding(SpentBytes: 0, AllowanceBytes: 500L * 1024 * 1024 * 1024));

    /// <summary>A workspace that has served exactly what it was sold, which is the edge that refuses.</summary>
    public static FixedEgressAllowance Spent() =>
        new(new EgressStanding(
            SpentBytes: 500L * 1024 * 1024 * 1024,
            AllowanceBytes: 500L * 1024 * 1024 * 1024));

    /// <summary>How many times the route asked. Zero is an answer: it means the gate was skipped.</summary>
    public int Reads { get; private set; }

    public Task<EgressStanding> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Reads++;

        return Task.FromResult(standing);
    }
}
