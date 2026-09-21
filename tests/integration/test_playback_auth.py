"""Regression tests for Jellyfin 12 playback authentication.

Jellyfin 12 removed every legacy authentication path: the ``api_key`` query
parameter, the ``X-Emby-Token``/``X-MediaBrowser-Token`` headers and the
``X-Emby-Authorization`` header. Only ``Authorization: MediaBrowser Token="..."``
is accepted, and it is also accepted by Jellyfin 10.11, so the plugin uses it
everywhere.

These tests cover the bug where the editor built the HLS URL with
``api_key=ApiClient.accessToken()`` and hls.js therefore received a 401 for
``/Videos/{id}/master.m3u8`` on Jellyfin 12.
"""
import os
import uuid

import pytest
import requests

from conftest import token_from_headers

JELLYFIN_URL = os.environ.get("JELLYFIN_URL", "http://localhost:8096")


def master_playlist_params(media_source_id):
    """Mirror the query parameters the config page sends for HLS playback."""
    return {
        "MediaSourceId": media_source_id,
        "VideoCodec": "h264",
        "AudioCodec": "aac",
        "MaxStreamingBitrate": 5000000,
        "PlaySessionId": str(uuid.uuid4()),
    }


@pytest.fixture(scope="module")
def media_source_id(admin_session, sample_item_id):
    """Return the default media source ID for the sample item."""
    headers, user_id = admin_session
    r = requests.get(
        f"{JELLYFIN_URL}/Users/{user_id}/Items/{sample_item_id}",
        params={"Fields": "Path,MediaSources"},
        headers=headers,
    )
    r.raise_for_status()
    sources = r.json().get("MediaSources", [])
    assert sources, "Sample item has no media sources"
    return sources[0]["Id"]


class TestPlaybackAuthentication:
    """The editor must authenticate playback with the Authorization header."""

    def test_master_playlist_succeeds_with_authorization_header(
        self, admin_session, sample_item_id, media_source_id
    ):
        """HLS playback must work when only the Authorization header is sent."""
        headers, _ = admin_session

        r = requests.get(
            f"{JELLYFIN_URL}/Videos/{sample_item_id}/master.m3u8",
            params=master_playlist_params(media_source_id),
            headers=headers,
            timeout=60,
        )

        assert r.status_code == 200, f"Expected 200, got {r.status_code}: {r.text}"
        assert r.text.lstrip().startswith("#EXTM3U")

    def test_master_playlist_rejects_query_parameter_token(
        self, admin_session, sample_item_id, media_source_id, server_major_version
    ):
        """Jellyfin 12 must reject the legacy api_key query parameter.

        This is the exact request the plugin used to make; it is what produced
        the reported 401. On Jellyfin 10.11 the parameter still authenticates,
        so the assertion is version-aware.
        """
        headers, _ = admin_session
        params = master_playlist_params(media_source_id)
        params["api_key"] = token_from_headers(headers)

        r = requests.get(
            f"{JELLYFIN_URL}/Videos/{sample_item_id}/master.m3u8",
            params=params,
            timeout=60,
        )

        if server_major_version >= 12:
            assert r.status_code == 401, (
                "Jellyfin 12 unexpectedly accepted api_key query auth; "
                f"got {r.status_code}"
            )
        else:
            assert r.status_code == 200, f"Expected 200, got {r.status_code}"

    def test_unauthenticated_master_playlist_is_rejected(
        self, sample_item_id, media_source_id
    ):
        """Sanity check that the endpoint is actually protected."""
        r = requests.get(
            f"{JELLYFIN_URL}/Videos/{sample_item_id}/master.m3u8",
            params=master_playlist_params(media_source_id),
            timeout=60,
        )
        assert r.status_code == 401


class TestPluginEndpointAuthentication:
    """Plugin endpoints must work with modern auth and reject legacy auth."""

    def test_waveform_endpoint_accepts_authorization_header(
        self, admin_session, sample_item_id
    ):
        """A 401 here would mean the modern header was not honoured."""
        headers, _ = admin_session

        r = requests.get(
            f"{JELLYFIN_URL}/SubtitleOffset/Waveform/{sample_item_id}",
            headers=headers,
        )

        assert r.status_code in (200, 404), f"Unexpected {r.status_code}: {r.text}"

    def test_generate_waveform_rejects_query_parameter_token(
        self, admin_session, sample_item_id, server_major_version
    ):
        """GenerateWaveform used to rely on api_key; it must not anymore."""
        headers, _ = admin_session

        r = requests.post(
            f"{JELLYFIN_URL}/SubtitleOffset/GenerateWaveform",
            params={"api_key": token_from_headers(headers)},
            json={"itemId": sample_item_id},
            timeout=120,
        )

        if server_major_version >= 12:
            assert r.status_code == 401
        else:
            assert r.status_code != 401

    def test_legacy_emby_authorization_header_is_rejected(
        self, admin_session, sample_item_id, server_major_version
    ):
        """X-Emby-Authorization is disabled by default on Jellyfin 12."""
        headers, _ = admin_session

        r = requests.get(
            f"{JELLYFIN_URL}/SubtitleOffset/Waveform/{sample_item_id}",
            headers={"X-Emby-Authorization": headers["Authorization"]},
        )

        if server_major_version >= 12:
            assert r.status_code == 401
        else:
            assert r.status_code in (200, 404)
