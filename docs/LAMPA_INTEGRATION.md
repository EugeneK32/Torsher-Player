# Lampa integration contract — v1

Lampa remains the source of truth for watch state. Torsher Player accepts identity/resume information and reports playback state back.

## Launch

```text
TorsherPlayer.exe
  (--url <media-url-or-file> | --torrent <file.torrent>)
  --media-key <stable-opaque-id>
  --title <display-title>
  --resume <seconds>
  --callback <http-endpoint>
  --callback-token <bearer-token>
```

Optional repeatable arguments:

```text
--sub-file <path-or-url>
--audio-file <path-or-url>
--http-header "Header: value"
```

Other options:

```text
--user-agent <value>
--referrer <url>
--progress-file <json-path>
--windowed
--ui-scale <0.9..2.5>
--completed-at <0..1>       # default 0.90
--progress-every <2..60>    # default 5 seconds
--torrent-file-index <N>     # optional zero-based media-file index
```

`media-key` is opaque to the player. Recommended example: `tmdb:60625:s09:e01`.


### Torrent launch semantics

`--url` and `--torrent` are mutually exclusive. A positional argument ending in `.torrent` is treated as a torrent path as well.

If the torrent contains multiple video files and `--torrent-file-index` is not supplied, Torsher Player opens an in-player file/episode picker before playback. The same list remains available from the playback toolbar so the user can change episodes without returning to Lampa.

For callback/checkpoint identity, Torsher Player derives a per-file key from the supplied `media-key`:

```text
<media-key>#file:<index>:<torrent-path-inside-package>
```

If Lampa does not supply `media-key`, a torrent/file fallback identity is generated. This prevents one episode's resume state from overwriting another episode.

## Callback

HTTP `POST`, `Content-Type: application/json`. If `--callback-token` is set, Torsher Player sends `Authorization: Bearer <token>`.

Events:

```text
started
file_loaded
playing
paused
seeked
progress
completed
stopped
error
```

Payload:

```json
{
  "version": 1,
  "event": "progress",
  "sessionId": "e653a975fb584aad",
  "mediaKey": "tmdb:60625:s09:e01",
  "title": "Rick and Morty — S09E01",
  "position": 812.4,
  "duration": 1333.088,
  "percent": 60.94,
  "paused": false,
  "watched": false,
  "volume": 100,
  "muted": false,
  "speed": 1.0,
  "audioTrack": {
    "id": 1,
    "type": "audio",
    "title": "сЫендук",
    "lang": "ru",
    "codec": "aac",
    "selected": true,
    "default": true,
    "forced": false,
    "external": false
  },
  "subtitleTrack": null,
  "error": null,
  "timestamp": "2026-08-09T18:30:00+00:00"
}
```

Callbacks are serialized by one worker and have a short timeout. A slow/dead Lampa endpoint therefore cannot stall the UI or libmpv.

## Resume/checkpoint behavior

Resume priority:

1. explicit `--resume` from Lampa;
2. local fallback checkpoint keyed by SHA-256 of `media-key`;
3. zero.

The local checkpoint is a crash/network fallback only. Lampa should persist the callback position and send it back on the next launch.

On natural EOF the player emits `completed`, flushes the callback queue, and exits back to Lampa. Closing early emits `stopped`.
