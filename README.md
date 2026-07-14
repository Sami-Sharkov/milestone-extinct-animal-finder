# Not Actually Extinct — Camera-Trap Species Rediscovery Detector

A MIP SDK component that watches XProtect cameras for motion, filters out
false positives with MegaDetector, and classifies detected animals with
SpeciesNet + BioCLIP to check for a match against a target species (e.g. a
presumed-extinct/rare species). Matches (and optionally non-matches, for
tuning) get logged in-app and posted to Discord.

This is a detection/alert tool for **human review** — false positives are
expected; nothing here should be treated as a confirmed sighting without a
person checking the crop image.

## Architecture

```
SpeciesDetector/            WPF app (.NET Framework 4.7.2)
  App.xaml.cs                Login, config loading, MIP SDK init
  MainWindow.xaml(.cs)       UI, motion/poll triggers, pipeline orchestration
  EventReceiver.cs           MIP SDK event subscription
  MotionEventFinder.cs       Looks up the "motion start" event type ID
  SnapshotGrabber.cs         Grabs a JPEG frame via JPEGLiveSource
  CameraStreamResolver.cs    Resolves a specific sub-stream, if configured
  MegaDetectorClient.cs      Calls the detection server's /detect
  ClassifierClient.cs        Calls the detection server's /classify
  DiscordClient.cs           Posts alert embeds to a Discord webhook
  DetectionLogger.cs         Appends a JSONL record per detection

  detection_server.py        Persistent FastAPI server: MegaDetector +
                              SpeciesNet + BioCLIP, loaded once at startup
  requirements.txt           Python dependencies for detection_server.py
  start_server.bat           Manual launcher (the app also auto-starts it)
  config.json                Local config — gitignored, has secrets
  config.example.json        Template — copy this to config.json
```

Pipeline: motion event or Auto Poll → grab a live JPEG → MegaDetector
(reject empty/false-positive frames) → crop → SpeciesNet + BioCLIP →
if BioCLIP matches the target species (or `discord_notify_non_target` is
on), post to Discord and show it in the app's Detections panel. Every
classified animal — match or not — gets appended to
`snapshots/detections_log.jsonl` regardless, for later dataset-building.

## Setup

1. **Python environment** (from the repo root):
   ```
   python -m venv venv
   venv\Scripts\pip install -r SpeciesDetector\requirements.txt
   ```
   First run of the server downloads the MegaDetector model (~200MB) and
   BioCLIP weights — expect a couple of minutes.

2. **Config**:
   ```
   copy SpeciesDetector\config.example.json SpeciesDetector\config.json
   ```
   Then edit `config.json`:
   - `target_species` / `candidate_species` — your target + distractor classes for BioCLIP
   - `discord_webhook_url` — leave empty if you don't want Discord alerts
   - `target_cameras` — name substrings to restrict monitoring to specific camera(s)/channel(s) on a shared server. Leave `[]` to monitor everything. The app logs every discovered camera's name on connect, so check there if you're not sure what to put here.
   - `camera_stream` — only relevant if a *single* camera exposes multiple sub-streams via `Camera.StreamFolder.Streams`; usually leave empty
   - `poll_interval_seconds` — Auto Poll interval; use this instead of motion detection for cameras/drivers that don't generate real motion events (e.g. synthetic/looped test video sources)

3. **Build & run**:
   ```
   cd SpeciesDetector
   dotnet run
   ```
   The app auto-starts `detection_server.py` if it isn't already running.
   On first launch it'll show a Milestone login dialog — Cancel it to run
   in Offline Test Mode (no live server, but "Test Local Image" still
   works for pipeline testing).

## Known limitations

- **Motion detection doesn't fire on synthetic/virtual test cameras** in
  our testing (a looped-video test rig never generated a motion event
  despite `MotionDetectionFolder` showing `Enabled=True`) — use Auto Poll
  for those instead.
- **Discord requires general internet access.** An isolated camera-only
  network typically has no route out, so the webhook will fail while
  connected to it — that's expected, not a bug. Use the in-app Detections
  panel instead in that case.
- A multi-channel virtual device's individual channels may not appear via
  the normal `Configuration.Instance` enumeration at all, even after a
  refresh — `MainWindow.FindTargetChannelsViaHardware` works around this
  by resolving through `Hardware.CameraFolder.Cameras` and fetching the
  Item directly by ID.
