using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The disk, which on this box is the binding constraint rather than an operational footnote.
///
/// <para>These run against a real temporary directory on purpose. A filesystem sweeper fails silently
/// far more easily than a table one — a wrong path, a permissions error, or a
/// <c>Directory.Exists</c> that returns false all produce exactly zero deletions and no exception —
/// so what has to be proven is that the code can delete at all.</para>
/// </summary>
public class TelegramWorkDirectoryTests : IDisposable
{
    private const long BotUserId = 123456789;

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "drive-union-workdir-tests",
        Guid.NewGuid().ToString("N"));

    private readonly FixedClock _clock = new(Now);
    private readonly TelegramTestHarness.FakeDiskSpace _disk = new();

    [Fact]
    public void The_sweeper_deletes_something_and_says_how_much()
    {
        var options = NewOptions();
        var sweeper = NewSweeper(options);

        SeedFile("stale-one", 4096, Now.AddMinutes(-90));
        SeedFile("stale-two", 8192, Now.AddMinutes(-45));

        var result = sweeper.Sweep(BotUserId);

        // A non-zero count, because zero deletions and no exception is exactly what a wrong path
        // looks like. In production zero is the good state — deletion on success does the normal
        // work — but that makes this test the only thing that proves the code can delete at all.
        result.FilesDeleted.Should().Be(2);
        result.BytesDeleted.Should().Be(4096 + 8192);
        result.FilesRemaining.Should().Be(0);
    }

    [Fact]
    public void A_directory_of_fresh_files_is_left_alone()
    {
        var options = NewOptions();
        var sweeper = NewSweeper(options);

        SeedFile("in-flight", 4096, Now.AddMinutes(-2));

        var result = sweeper.Sweep(BotUserId);

        // The inverse, and the one that matters more here: the backstop must not eat an in-flight
        // transfer. Thirty minutes is comfortably past the longest legitimate hold, which is one
        // ceiling-sized transfer and its retries.
        result.FilesDeleted.Should().Be(0);
        result.FilesRemaining.Should().Be(1);
        result.BytesRemaining.Should().Be(4096);
    }

    [Fact]
    public void Below_the_watermark_the_oldest_go_regardless_of_age()
    {
        var options = NewOptions();
        options.WorkDirMinFreeBytes = 5_000_000_000;
        _disk.FreeBytes = 1_000_000;

        var sweeper = NewSweeper(options);

        SeedFile("young-one", 1024, Now.AddSeconds(-30));
        SeedFile("young-two", 2048, Now.AddSeconds(-10));

        var result = sweeper.Sweep(BotUserId);

        // Deleting a file that is thirty seconds old is destructive — it may be an in-flight
        // transfer, which will then fail — and that is the correct trade: a failed transfer is one
        // error message, and a full volume takes the database and the upload spool down with it.
        result.FilesDeleted.Should().Be(2);
    }

    [Fact]
    public void A_missing_directory_is_not_an_exception_and_not_a_pretend_success()
    {
        var options = NewOptions();
        options.WorkDirectory = Path.Combine(_root, "never-created");

        var result = NewSweeper(options).Sweep(BotUserId);

        result.FilesDeleted.Should().Be(0);
        result.FilesRemaining.Should().Be(0);
    }

    [Fact]
    public void The_pre_flight_refuses_a_transfer_the_volume_cannot_hold()
    {
        var options = NewOptions();
        options.WorkDirHeadroomBytes = 1_000_000_000;
        options.WorkDirMinFreeBytes = 100;
        _disk.FreeBytes = 1_500_000_000;

        var directory = NewSweeper(options);

        directory.HasRoomFor(400_000_000).Should().BeTrue();
        directory.HasRoomFor(900_000_000).Should().BeFalse();
    }

    [Fact]
    public void Below_the_watermark_nothing_byte_moving_is_accepted_at_all()
    {
        var options = NewOptions();
        options.WorkDirMinFreeBytes = 2_000_000_000;
        _disk.FreeBytes = 1_000_000_000;

        // Not even a small one, and that is the point of a watermark rather than a per-file check:
        // below it the volume needs to recover before anything else is added to it.
        NewSweeper(options).HasRoomFor(1024).Should().BeFalse();
    }

    [Fact]
    public void With_no_local_server_there_is_no_directory_and_nothing_to_run_out_of()
    {
        var options = NewOptions();
        options.WorkDirectory = null;

        var directory = NewSweeper(options);

        // Development has no local Bot API server, so the bytes never land here. That is not a hole:
        // the branch that writes to this volume is the branch that reads a local path, and it does
        // not exist unless the server does.
        directory.PathFor(BotUserId).Should().BeNull();
        directory.HasRoomFor(long.MaxValue).Should().BeTrue();
        directory.Measure(BotUserId).Should().Be((0L, 0, (TimeSpan?)null));
    }

    [Fact]
    public void The_measurement_is_what_the_operators_alarm_reads()
    {
        var options = NewOptions();
        SeedFile("held", 4096, Now.AddMinutes(-10));

        var (bytes, files, oldest) = NewSweeper(options).Measure(BotUserId);

        // A size that stays above zero across several minutes is what a stopped delete-on-success
        // looks like while everything about the bot appears perfectly healthy, and it is the alarm
        // that ends the feature by filling the volume the database is on.
        bytes.Should().Be(4096);
        files.Should().Be(1);
        oldest.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void The_path_swept_is_the_bots_own_subdirectory()
    {
        var options = NewOptions();

        // The server organises files per bot, keyed by the bot's numeric id, so a second bot's files
        // are neither measured nor swept by the first one's pass.
        NewSweeper(options).PathFor(BotUserId)
            .Should().Be(Path.Combine(_root, BotUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (!Directory.Exists(_root)) return;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives one test run is not a test failure.
        }
    }

    private TelegramOptions NewOptions() => new()
    {
        WorkDirectory = _root,
        WorkDirMaxAgeMinutes = 30,
        WorkDirMinFreeBytes = 1,
        WorkDirHeadroomBytes = 1,
    };

    private TelegramWorkDirectory NewSweeper(TelegramOptions options) => new(
        Options.Create(options),
        _disk,
        _clock,
        NullLogger<TelegramWorkDirectory>.Instance);

    private void SeedFile(string name, int size, DateTimeOffset written)
    {
        var directory = Path.Combine(
            _root,
            BotUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }
}
