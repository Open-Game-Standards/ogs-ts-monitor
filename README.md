# OGS-TS Monitor

> [!IMPORTANT]
> **In active development.** Tested against the standard's conformance
> corpus and its own demo sender; first releases. Please report what you
> find on [Discord](https://discord.gg/tseF4Q6U99), especially a
> conformance finding you think is wrong: if the monitor misreads the
> standard, it misleads everyone building a sender.
>
> Development is funded by
> [sponsorship](https://github.com/sponsors/BrianGilbert), which moves this
> and the rest of the [OGS](https://github.com/Open-Game-Standards)
> work forward.

A cross-platform desktop receiver for the
[OGS Telemetry Standard (OGS-TS)](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard):
it joins the multicast group, shows every sender on your network, and checks
everything it receives against the standard as it goes.

![OGS-TS Monitor showing the built-in demo car](docs/screenshot.png)

Use it to see what a game is sending, to check your own sender while you
build it, or to confirm a loader mod is working before you strap in.

## What it shows

- **Senders and objects:** every sender on the network, told apart by
  source address and port, with its session (game, instance label, host,
  schema, environment, gravity, discovery generation and pages) and its
  objects, with live motion rates. Two games publishing the same object
  name stay separate. Paged discovery and motion parts merge
  into one picture: pages of a generation by object and channel id, a
  tick's parts by timestamp.
- **Attitude:** two views, one click apart. **World** shows the body axes
  against fixed world axes, for vehicles, spacecraft and free 6-DoF motion; it
  works at any attitude, including inverted or vertical (the default for
  non-aircraft). **Horizon** is an aircraft attitude indicator with a heading
  tape (the default for aircraft).
- **Track:** the last 30 s of position from above, auto-fitted, with heading.
- **Time history:** auto-scaled strips of roll, pitch, yaw rate, surge, sway,
  heave and height, so a car's 2° of roll is as readable as an aircraft's 60°.
- **Channels:** every declared channel with its live value, unit and range.
- **Stream, events and conformance:** a throttled frame log, deduplicated
  events with their replay counts, and conformance findings.

## Conformance checks

Findings are aggregated, so a sender repeating a mistake counts up one row
rather than flooding the list.

| Rule | Level | Checks |
| --- | --- | --- |
| `schema-version` | warning | discovery `schemaVersion` is 1 |
| `gravity-magnitude` | warning | `gravity` equals \|`gravityVector`\| (CONVENTIONS, Gravity) |
| `no-primary` | warning | discovery declares a primary object |
| `duplicate-object`, `duplicate-channel-id` | warning | names and ids identify one thing |
| `quaternion-norm` | warning | orientation is a unit quaternion |
| `primary-first` | warning | motion lists the primary object first |
| `motion-rate` | warning | the achieved motion rate (from frame timestamps, over a few seconds) is within 10 % of the declared `motionRateHz` (implementation-udp.md, Send cadence) |
| `motion-gap` | warning | more than 5 % of the declared motion ticks are missing over the last 10 s, estimated from gaps in the frames' own timestamps (a refused send and a dropped packet look the same to a receiver); the live figure is on the session line |
| `motion-jitter` | warning | motion arrives with a 95th-percentile delay variation under 50 ms over the last 10 s (arrival minus sender timestamp, above the window's lowest value, so no clock sync is needed); the session line also shows an apparent one-way delay that is right only when sender and monitor clocks agree |
| `undeclared-object`, `undeclared-channel` | warning | motion only refers to what discovery declared |
| `event-id-order` | warning | new event ids never go backwards (session-monotonic) |
| `event-intensity` | warning | event intensity is within 0..1 (C-3) |
| `datagram-budget` | warning | every datagram is at most 1,400 bytes (implementation-udp.md, Datagram budget) |
| `channel-type` | warning | a channel's values use the variant its descriptor declares |
| `generation-not-bumped` | warning | descriptors change only under a new discovery generation |
| `page-index`, `part-index` | warning | a page or part index is below its count |
| `generation-step` | note | a new generation is the previous one plus one (otherwise discovery was missed or the sender restarted) |
| `motion-before-discovery` | note | motion arriving before anything to interpret it against |
| `out-of-range` | note | a value outside its declared operating range |
| `size-envelope` | note | a sender stays within ~105 KB/s and ~30 KB of discovery (implementation-udp.md, Sender size envelope) |

The checks follow the reference receiver in
[ogs-ts-dotnet](https://github.com/Open-Game-Standards/ogs-ts-dotnet) and the
schema, and are tested against the standard's conformance corpus.

## Trying it without a game

**Start demo sender** publishes a demo car with body dynamics, channels and
impact events. It is isolated: it sends over the loopback interface on its
own group and port (239.255.17.99:40199) with its own receiver, so nothing
else on this machine or your network sees it — a GameConn instance driving a
motion rig won't move — and a game streaming alongside it isn't disturbed.
To send the demo on your network as a real sender, start with `--demo-lan`.

The isolated demo has been checked on Windows. Linux often ships with
multicast disabled on the loopback interface; if the demo shows nothing
there, use `--demo-lan` on a network with no rig attached.

## Recording and playback

**Record** saves everything the monitor receives (the live stream and the
demo alike) to an `.ogsrec` file, the standard's recording format
([recording.md](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/recording.md)):
attach one to a bug report, or replay it into a rig with GameConn.
**Play recording** shows a recording in this monitor at its original pace,
each recorded sender as its own session. Playback stays inside the
monitor: nothing is sent on the network, so no rig moves.

## Running

Download a build from
[Releases](https://github.com/Open-Game-Standards/ogs-ts-monitor/releases).
Nothing to install alongside: each is self-contained.

| Platform | Download | Run |
| --- | --- | --- |
| Windows (x64) | `OgsTsMonitor-<version>-win-x64.zip` | unzip, run `OgsTsMonitor.exe` |
| Linux (x64) | `OgsTsMonitor-<version>-linux-x64.tar.gz` | `tar -xzf …` then `./OgsTsMonitor` |
| macOS (Apple silicon / Intel) | `OgsTsMonitor-<version>-osx-arm64.zip` / `-osx-x64.zip` | unzip, move **OGS-TS Monitor** to Applications |

The macOS app is signed with a Developer ID and notarized by Apple. On
macOS 15 and later it asks for **Local Network** access the first time:
allow it, since OGS-TS arrives as multicast on your LAN.

```
OgsTsMonitor [--group 239.255.17.17] [--port 40123] [--demo | --demo-lan]
```

`--group` also takes the standard's IPv6 group, `ff12::4f47:5453`; IPv6
joins on the default interface.

On a machine with several network interfaces, pick the one facing your LAN
in **Interface**: the default route's interface may have no path to the
sender. The monitor also joins on loopback, to hear senders on this machine.

## Building

The project references ogs-ts-dotnet by path, and the tests decode the
standard's conformance corpus, so check the three out side by side:

```
Code/
└── Open-Game-Standards/
    ├── ogs-ts-monitor/          ← this repo
    ├── ogs-ts-dotnet/           ← Ogs.Telemetry.Receiving
    └── OGS-Telemetry-Standard/  ← conformance corpus
```

```sh
dotnet build OgsTsMonitor.slnx
dotnet test tests/Ogs.Telemetry.Monitor.Tests
dotnet run --project src/OgsTsMonitor -- --demo
```

Requires the .NET 10 SDK. CI builds, tests and packages on Windows, Linux
and macOS for every push and pull request (each run's artifacts hold the
apps). Pushes to `main` publish a CalVer release (`vYYYY.MM.DD`), as the
loader mods do. The macOS app is assembled, signed and notarized by
`build/macos/bundle.sh` in a separate job that runs in the `release`
environment: only that job sees the Developer ID, and a maintainer approves
it before anything is released.

## Layout and reuse

| Project | What |
| --- | --- |
| `Ogs.Telemetry.Monitor.Core` | UI-free: `StreamTracker` folds decoded frames into snapshots and motion history and runs the conformance checks |
| `Ogs.Telemetry.Monitor` | Avalonia controls: `MonitorView` and the attitude, track and strip-chart views |
| `OgsTsMonitor` | The app: receiver, endpoint settings, demo sender |

Other .NET apps can embed the monitor. Feed a `StreamTracker` from a
`TelemetryReceiver` (or call `Ingest` with raw datagrams) and show it:

```csharp
var tracker = new StreamTracker().Attach(receiver);
var view = new MonitorView { DataContext = new MonitorViewModel(tracker) };
```

## License

MIT. See [LICENSE](LICENSE).

## Faults

A fault on the UI thread (a layout pass that cannot arrange a panel after an
unusual resize, say) does not end the monitor: the stream keeps being tracked
and the fault is appended to `%LOCALAPPDATA%\OgsTsMonitor\faults.log`.

Apps that embed the monitor views call `CrashGuard.Install("<AppName>")` at startup to get the same behaviour, logging to `%LOCALAPPDATA%\<AppName>\faults.log`.
