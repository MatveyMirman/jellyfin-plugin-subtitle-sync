using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubtitleOffset.Services;
using Jellyfin.Plugin.SubtitleOffset.Srt;
using Xunit;

namespace Jellyfin.Plugin.SubtitleOffset.Tests;

/// <summary>
/// Tests for non-uniform subtitle edits: ripple shifts and per-cue duration changes.
/// These cannot be expressed by the single offset encoded in the .Offset+1234ms filename,
/// so they are sent to the server as explicit per-entry timings matched by ordinal.
/// </summary>
public class ExplicitTimingTests
{
    private static List<SrtEntry> BuildEntries()
    {
        // 4 cues, each 1s long, starting at 1s, 5s, 10s, 15s.
        return new List<SrtEntry>
        {
            NewEntry(1, 1000, 2000, "one"),
            NewEntry(2, 5000, 6000, "two"),
            NewEntry(3, 10000, 11000, "three"),
            NewEntry(4, 15000, 16000, "four")
        };
    }

    private static SrtEntry NewEntry(int sequence, long startMs, long endMs, string text) => new()
    {
        SequenceNumber = sequence,
        StartTime = TimeSpan.FromMilliseconds(startMs),
        EndTime = TimeSpan.FromMilliseconds(endMs),
        TextLines = new[] { text }
    };

    private static SubtitleTimingDto Timing(int ordinal, long startMs, long endMs) => new()
    {
        Ordinal = ordinal,
        StartMs = startMs,
        EndMs = endMs
    };

    private static long StartMs(SrtEntry entry) => (long)entry.StartTime.TotalMilliseconds;

    private static long EndMs(SrtEntry entry) => (long)entry.EndTime.TotalMilliseconds;

    [Fact]
    public void ApplyExplicitTimings_RippleFromThirdCue_LeavesEarlierCuesUntouched()
    {
        var entries = BuildEntries();

        // Drag cue 3 (ordinal 2) forward by 2s; cues 3 and 4 move, 1 and 2 stay put.
        OffsetFileService.ApplyExplicitTimings(entries, new[]
        {
            Timing(2, 12000, 13000),
            Timing(3, 17000, 18000)
        });

        Assert.Equal(1000, StartMs(entries[0]));
        Assert.Equal(2000, EndMs(entries[0]));
        Assert.Equal(5000, StartMs(entries[1]));
        Assert.Equal(6000, EndMs(entries[1]));

        Assert.Equal(12000, StartMs(entries[2]));
        Assert.Equal(13000, EndMs(entries[2]));
        Assert.Equal(17000, StartMs(entries[3]));
        Assert.Equal(18000, EndMs(entries[3]));
    }

    [Fact]
    public void ApplyExplicitTimings_PreservesTextAndSequenceNumbers()
    {
        var entries = BuildEntries();

        OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(1, 7000, 9000) });

        Assert.Equal(new[] { 1, 2, 3, 4 }, entries.Select(e => e.SequenceNumber));
        Assert.Equal(new[] { "one", "two", "three", "four" }, entries.Select(e => e.TextLines[0]));
    }

    [Fact]
    public void ApplyExplicitTimings_ChangesSingleCueDurationWithoutMovingOthers()
    {
        var entries = BuildEntries();

        // Extend cue 2 by 1.5s, leaving every other cue alone.
        OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(1, 5000, 7500) });

        Assert.Equal(5000, StartMs(entries[1]));
        Assert.Equal(7500, EndMs(entries[1]));
        Assert.Equal(10000, StartMs(entries[2]));
        Assert.Equal(15000, StartMs(entries[3]));
    }

    [Fact]
    public void ApplyExplicitTimings_OmittedEntriesKeepOriginalTimes()
    {
        var entries = BuildEntries();

        OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(0, 1500, 2500) });

        Assert.Equal(1500, StartMs(entries[0]));
        Assert.Equal(5000, StartMs(entries[1]));
        Assert.Equal(10000, StartMs(entries[2]));
        Assert.Equal(15000, StartMs(entries[3]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void ApplyExplicitTimings_RejectsOutOfRangeOrdinal(int ordinal)
    {
        var entries = BuildEntries();

        Assert.Throws<ArgumentException>(
            () => OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(ordinal, 1000, 2000) }));
    }

    [Fact]
    public void ApplyExplicitTimings_RejectsDuplicateOrdinal()
    {
        var entries = BuildEntries();

        Assert.Throws<ArgumentException>(() => OffsetFileService.ApplyExplicitTimings(entries, new[]
        {
            Timing(1, 5000, 6000),
            Timing(1, 7000, 8000)
        }));
    }

    [Fact]
    public void ApplyExplicitTimings_RejectsNegativeStart()
    {
        var entries = BuildEntries();

        Assert.Throws<ArgumentException>(
            () => OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(0, -500, 2000) }));
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(6000, 5000)]
    public void ApplyExplicitTimings_RejectsNonPositiveDuration(long startMs, long endMs)
    {
        var entries = BuildEntries();

        Assert.Throws<ArgumentException>(
            () => OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(1, startMs, endMs) }));
    }

    [Fact]
    public void ApplyExplicitTimings_RejectsShiftBeyondMaximum()
    {
        var entries = BuildEntries();

        // Default maximum shift is 600000ms (10 minutes).
        Assert.Throws<ArgumentException>(
            () => OffsetFileService.ApplyExplicitTimings(entries, new[] { Timing(0, 900_000, 901_000) }));
    }

    [Fact]
    public void ApplyExplicitTimings_LeavesEntriesUnchangedWhenTimingListIsEmpty()
    {
        var entries = BuildEntries();

        OffsetFileService.ApplyExplicitTimings(entries, Array.Empty<SubtitleTimingDto>());

        Assert.Equal(new long[] { 1000, 5000, 10000, 15000 }, entries.Select(StartMs));
    }
}

/// <summary>
/// Tests for the user-supplied file name used when saving non-uniform edits to a new file.
/// </summary>
public class SanitizeFileNameTests
{
    [Theory]
    [InlineData("movie.en.Resync.srt", "movie.en.Resync.srt")]
    [InlineData("  spaced.srt  ", "spaced.srt")]
    public void SanitizeFileName_KeepsValidNames(string input, string expected)
    {
        Assert.Equal(expected, OffsetFileService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("movie.en.Resync", "movie.en.Resync.srt")]
    [InlineData("custom", "custom.srt")]
    public void SanitizeFileName_AppendsSrtExtension(string input, string expected)
    {
        Assert.Equal(expected, OffsetFileService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("../../etc/passwd.srt", "passwd.srt")]
    [InlineData("/etc/evil.srt", "evil.srt")]
    [InlineData("sub/dir/name.srt", "name.srt")]
    public void SanitizeFileName_StripsDirectoryTraversal(string input, string expected)
    {
        Assert.Equal(expected, OffsetFileService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("/")]
    public void SanitizeFileName_RejectsEmptyOrRelativeNames(string input)
    {
        Assert.Throws<ArgumentException>(() => OffsetFileService.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_RejectsInvalidCharacters()
    {
        Assert.Throws<ArgumentException>(() => OffsetFileService.SanitizeFileName("bad\0name.srt"));
    }
}
