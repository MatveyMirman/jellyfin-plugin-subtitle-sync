using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.SubtitleOffset.Tests;

/// <summary>
/// Guards the authentication scheme used by the embedded admin config page.
///
/// Jellyfin 12 removed the <c>api_key</c> query parameter, the
/// <c>X-Emby-Token</c>/<c>X-MediaBrowser-Token</c> headers and the
/// <c>X-Emby-Authorization</c> header. Only
/// <c>Authorization: MediaBrowser Token="..."</c> is accepted, and Jellyfin 10.11
/// accepts it too. Requests the page makes outside of ApiClient (hls.js and the
/// waveform XMLHttpRequest) must therefore set that header themselves.
/// </summary>
public class ConfigPageAuthTests
{
    private static readonly string ConfigPage = LoadConfigPage();

    private static string LoadConfigPage()
    {
        var assembly = typeof(Plugin).Assembly;
        const string ResourceName = "Jellyfin.Plugin.SubtitleOffset.Configuration.configPage.html";

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetSetupVideoBody()
    {
        var start = ConfigPage.IndexOf("function setupVideo(", StringComparison.Ordinal);
        Assert.True(start >= 0, "setupVideo() was not found in configPage.html");

        var end = ConfigPage.IndexOf("function destroyHls(", start, StringComparison.Ordinal);
        Assert.True(end > start, "destroyHls() was not found after setupVideo()");

        return ConfigPage[start..end];
    }

    [Fact]
    public void ConfigPage_ExposesAuthorizationHeaderHelper()
    {
        Assert.Contains("function getAuthorizationHeader()", ConfigPage, StringComparison.Ordinal);
        Assert.Contains("setRequestHeaders", ConfigPage, StringComparison.Ordinal);
        Assert.Contains("'MediaBrowser '", ConfigPage, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupVideo_DoesNotPutApiKeyInHlsSourceUrl()
    {
        var setupVideo = GetSetupVideoBody();
        var hlsSection = setupVideo[..setupVideo.IndexOf("window.Hls.isSupported()", StringComparison.Ordinal)];

        Assert.DoesNotContain("api_key", hlsSection, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupVideo_SendsAuthorizationHeaderForEveryHlsRequest()
    {
        var setupVideo = GetSetupVideoBody();

        Assert.Contains("xhrSetup", setupVideo, StringComparison.Ordinal);
        Assert.Contains("xhr.setRequestHeader('Authorization', authHeader)", setupVideo, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupVideo_SurfacesFatalPlaybackErrors()
    {
        var setupVideo = GetSetupVideoBody();

        Assert.Contains("showPlaybackWarning", setupVideo, StringComparison.Ordinal);
        Assert.Contains("401", setupVideo, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateWaveform_RequestUsesAuthorizationHeaderInsteadOfApiKey()
    {
        Assert.Contains("ApiClient.getUrl('SubtitleOffset/GenerateWaveform')", ConfigPage, StringComparison.Ordinal);
        Assert.Contains("xhr.setRequestHeader('Authorization', waveformAuthHeader)", ConfigPage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Emby-Token")]
    [InlineData("X-MediaBrowser-Token")]
    [InlineData("X-Emby-Authorization")]
    public void ConfigPage_DoesNotUseLegacyAuthHeaders(string legacyHeader)
    {
        Assert.DoesNotContain(legacyHeader, ConfigPage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigPage_OnlyUsesApiKeyOnTheNativeVideoFallback()
    {
        var setupVideo = GetSetupVideoBody();
        var fallbackStart = setupVideo.IndexOf("window.Hls.isSupported()", StringComparison.Ordinal);

        // The only remaining usage is the non-MSE <video> fallback, which cannot
        // send headers and therefore only works on Jellyfin < 12.
        Assert.Contains("legacyParams.api_key", setupVideo[fallbackStart..], StringComparison.Ordinal);

        var outsideSetupVideo = ConfigPage.Replace(setupVideo, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", outsideSetupVideo, StringComparison.Ordinal);
    }
}
