using System;
using Jellyfin.Plugin.SubtitleOffset.Api;
using Jellyfin.Plugin.SubtitleOffset.Services;
using Xunit;

namespace Jellyfin.Plugin.SubtitleOffset.Tests;

public class GenerateRequestTests
{
    [Fact]
    public void ExplicitTimingsAllowZeroUniformOffset()
    {
        var request = new GenerateRequest
        {
            ItemId = Guid.NewGuid(),
            OffsetMs = 0,
            Entries =
            [
                new SubtitleTimingDto { Ordinal = 0, StartMs = 1000, EndMs = 2000 }
            ]
        };

        Assert.True(request.HasExplicitTimings);
    }

    [Fact]
    public void EmptyTimingsDoNotCountAsExplicitEdits()
    {
        var request = new GenerateRequest
        {
            ItemId = Guid.NewGuid(),
            OffsetMs = 0,
            Entries = []
        };

        Assert.False(request.HasExplicitTimings);
    }
}
