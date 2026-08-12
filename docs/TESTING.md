# Testing

## Fast repository checks

These checks need Python only:

```bash
python3 scripts/verify-source.py
```

They validate AXAML/XML/JSON syntax, dynamic resource defaults, click handlers, native manifest checksums, target RIDs and the absence of known legacy architecture tokens.

## CI compile matrix

Pull requests compile all release RIDs in the GitHub Actions matrix: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. Release tags publish the same matrix and package release archives.

## Playback battle-test checklist

Use a portable/non-single-file build first so native dependency problems are obvious.

1. Start a TorrServer MKV and let it play for 10 minutes.
2. Pause/resume 20 times, including 30–60 second pauses. Resume should react immediately and must not reload/seek the stream.
3. Leave the OSD alone: it must hide after ~4 seconds, including while paused.
4. Move the mouse repeatedly and scrub the seekbar aggressively; UI must remain responsive.
5. Navigate audio tracks repeatedly between different codecs if available.
6. Enable/disable/change subtitles; adjust delay and size.
7. Navigate the entire UI using only arrows + Enter/Esc, then only D-pad/left stick + A/B. The focused element must always be visually obvious.
8. From any bottom OSD button press Up: focus must land on the seek bar. A short Left/Right tap must seek by 10 seconds. Holding either direction must move the preview smoothly with increasing speed and perform one real seek on release; Down must return to the playback controls.
9. Open Settings/Audio/Subtitles/Episodes with Enter/A and immediately press arrows. Focus must stay inside the opened panel until Esc/B closes it.
10. Start Torsher Player a second time while the first instance is running. No second window may appear.
11. Switch through all 13 themes while playing and verify icon/control contrast in each theme. In particular, Light, Paper and Ivory must remain visually distinct and readable.
12. Hot-unplug/reconnect the controller. Playback must continue and SDL should reconnect without restarting the player.
13. Change UI scale from 0.9× through 2.5× while playing.
14. Open the volume popover from the OSD, adjust it with pointer and keyboard/gamepad, and verify it closes cleanly without losing toolbar focus.
15. Enter/leave fullscreen several times.
16. Disconnect/reconnect the media source to validate in-player Retry/Error behavior.
17. Quit mid-episode; relaunch with the same `media-key` and no `--resume` to validate local fallback.
18. Run with a Lampa callback endpoint and verify ordered progress events.
19. Let a stream genuinely underrun: the buffering badge should appear only for cache pause, not ordinary user pause.
20. Open a `.torrent` containing several video files, select multiple episodes in turn, seek in each, and verify each gets independent resume state.
21. Complete one torrent episode and verify the in-player episode list appears instead of exiting immediately.
22. Test a slow/high-bitrate 4K HTTP or torrent source. The loading panel must keep waiting with live status, and transient startup failures should retry automatically before the terminal error UI appears.

## Cross-platform smoke tests

For each supported OS, verify:

- startup with a local H.264/AAC MP4;
- startup with an HEVC/MKV sample;
- libmpv native discovery from the expected source (bundled on Windows, system loader on Unix-like systems);
- SDL3 present -> controller works;
- SDL3 absent -> app still starts and keyboard/mouse work;
- fullscreen/windowed transitions;
- sleep/display inhibition is best-effort and does not crash when the OS tool is absent.

Log location is under the platform's local application-data directory in `TorsherPlayer/logs/torsherplayer.log`.
