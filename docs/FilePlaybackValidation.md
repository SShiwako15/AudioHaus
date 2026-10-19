# Independent local-file playback checkpoint

Validated on October 17, 2026, using the Realtek speakers and WH-1000XM6 headphones
on the same Windows 11 x64 PC used for the earlier tone checkpoint.

## Files and routing

| Player | Local file | Output |
| --- | --- | --- |
| 1 | `C:\Users\Legion\Music\Kendrick Lamar - HUMBLE..mp3` | Speakers (Realtek(R) Audio) |
| 2 | `C:\Users\Legion\Music\Bedroom - In My head.m4a` | Headphones (WH-1000XM6) |

Media Foundation decoded both files to 48 kHz stereo floating-point PCM. Reported
durations were 183.670 seconds for the MP3 and 359.584 seconds for the M4A. Each
player owned its own decoder, WASAPI stream, control state, and MTA worker.

The 30-second listening check sought player 1 to 15 seconds and player 2 to
30 seconds, then ran these phases at gain 0.15:

1. Both tracks for 12 seconds.
2. Player 1 paused for 5 seconds while player 2 continued.
3. Player 1 resumed, with both tracks playing for 8 seconds.
4. Player 2 stopped and reset to zero while player 1 continued for 5 seconds.
5. Both players stopped, reset to zero, and closed successfully.

The user confirmed that both tracks reached their assigned physical outputs and
that the independent pause/resume/stop controls worked. Both players retained
HRESULT `0x00000000` throughout the session. Lifetime frame counters after Stop
were 1,157,222 and 1,171,776 respectively; stopping reset media position without
resetting these diagnostic counters.

## Repeat the session

From the repository root, after building `AudioHaus.sln` for Release/x64:

```powershell
$speaker = '{0.0.0.00000000}.{d607b8e4-57c2-43f2-a0a8-80fb4a9e3fc7}'
$headphones = '{0.0.0.00000000}.{6657aafe-8f28-416c-839d-45d01af5aff7}'
$mp3 = 'C:\Users\Legion\Music\Kendrick Lamar - HUMBLE..mp3'
$m4a = 'C:\Users\Legion\Music\Bedroom - In My head.m4a'
.\artifacts\bin\x64\Release\AudioHaus.Diagnostics\AudioHaus.Diagnostics.exe files --file $mp3 --output $speaker --file $m4a --output $headphones --gain 0.15
```

In the session, enter `play all`, `pause 1`, `seek 1 30`, `play 1`, `stop 2`,
`status`, or `quit`. Player numbers follow command-line order. Commands affect
only the selected player; `all` is available for play/pause/stop. These endpoint
IDs are specific to this PC; use `list` to obtain current IDs if Windows recreates
them. User music files are read only and are not copied into the repository.

## Automated validation

| Check | Evidence |
| --- | --- |
| Debug/Release solution builds | Passed with warnings treated as errors. |
| WAV conversion | Generated PCM16 mono 22.05 kHz and stereo 44.1 kHz fixtures decoded to 48 kHz stereo. |
| Transport lifecycle | Open stopped, play, pause/frozen position, resume, stop/reset, and cumulative frame counts passed. |
| Seeking | Stopped, paused, and playing seeks passed; oversized seeks clamped to duration. |
| End-of-file | Drain, Ended state, seek below end, and Play-from-Ended restart passed. |
| Real codecs | Actual MP3 and M4A metadata, decoding, pause, seek/resume, and stop passed at silent gain. |
| Independent hardware players | Both progressed while playing; controlling player 1 left player 2's position and PCM submission advancing. |
| ABI | 56-byte status and all nine field offsets verified. |
| Error and lifetime handling | Invalid arguments, missing/corrupt files, invalid endpoint, repeated disposal, and calls after disposal passed. |
| CLI | 86 parsing/stopped-session/seek/cleanup checks and 24 timed independence assertions passed across Debug and Release. |
| Existing tone interface | Nonplaying argument, ABI, and native buffer-boundary regression checks passed. |

Run the repeatable tests after building the solution:

```powershell
dotnet run --project tests/AudioHaus.FilePlaybackChecks/AudioHaus.FilePlaybackChecks.csproj -c Release -- --file-a $mp3 --file-b $m4a
dotnet run --project tests/AudioHaus.FilePlaybackChecks/AudioHaus.FilePlaybackChecks.csproj -c Debug -- --hardware --file-a $mp3 --file-b $m4a --output-a $speaker --output-b $headphones
```

Default fixture and codec tests use gain zero on an actual output. `--hardware`
uses gain 0.15 and requires two distinct active outputs. Generated fixtures are
retained in unique temporary directories. The CLI case runner was temporary;
the file playback validation console is included in the repository.

## Technical boundaries

This milestone provides independent local-file transports through the console
interface. Bluetooth buffering can delay acoustic response after a control;
no acoustic synchronization guarantee is made. Mixing, fan-out routing, process
capture, a graphical interface, and extended disconnect/endurance testing belong
to later milestones. M4A support depends on its contained codec; this PC's actual
M4A was validated successfully.

The decoder follows Microsoft's [Source Reader decoding guidance](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--decoding-audio)
and [media-type/resampling documentation](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-setcurrentmediatype).
