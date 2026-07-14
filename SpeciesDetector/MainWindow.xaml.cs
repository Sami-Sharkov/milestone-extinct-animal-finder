#pragma warning disable CS4014
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VideoOS.Platform;
using VideoOS.Platform.EventsAndState;

namespace SpeciesDetector
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<string> _log = new ObservableCollection<string>();
        private int _eventCount  = 0;
        private int _animalCount = 0;
        private int _targetCount = 0;

        private DispatcherTimer _pollTimer;

        // Snapshots land here, relative to wherever the exe runs from.
        private static readonly string SnapshotFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");

        // Resolved once per camera (config_json "camera_stream") and reused —
        // avoids re-querying the config API on every single motion event.
        private readonly Dictionary<Guid, Guid?> _streamIdCache = new Dictionary<Guid, Guid?>();

        // A lingering animal re-triggers motion many times per minute; without a
        // per-camera cooldown each one would spawn its own MegaDetector/classify
        // run and, on a match, its own Discord alert.
        private static readonly TimeSpan CameraCooldown = TimeSpan.FromSeconds(20);
        private readonly ConcurrentDictionary<Guid, DateTime> _lastTriggered = new ConcurrentDictionary<Guid, DateTime>();

        // Null = unrestricted (monitor every camera on the server). Populated from
        // config.json's "target_cameras" so a shared test server's other cameras
        // (someone else's project, misc test items) don't get processed.
        private HashSet<Guid> _targetCameraIds;

        private Guid? ResolveStreamId(Item cameraItem)
        {
            if (_streamIdCache.TryGetValue(cameraItem.FQID.ObjectId, out var cached))
                return cached;

            var resolved = CameraStreamResolver.Resolve(cameraItem, App.Config.CameraStream, Log);
            _streamIdCache[cameraItem.FQID.ObjectId] = resolved;
            return resolved;
        }

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _log;

            // Show the actual target species from config
            TargetSpeciesText.Text = $"Target: {App.Config.TargetSpecies}";

            _pollTimer          = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(10);
            _pollTimer.Tick    += PollTimer_Tick;

            Loaded += MainWindow_Loaded;
        }

        // -----------------------------------------------------------------------
        // Startup: ensure detection server is running, then subscribe to events
        // -----------------------------------------------------------------------

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ── Step 1: start / verify the Python detection server ───────────
            StatusText.Text       = "Starting detection server...";
            ServerStatusText.Text = "Detection server: starting...";
            AddLog("Checking detection server — this may take ~2 min on first run (loading + warming up models)...");

            bool serverReady = await DetectionServerManager.EnsureRunningAsync(msg =>
                Dispatcher.BeginInvoke(new Action(() => AddLog(msg))));

            if (!serverReady)
            {
                AddLog("ERROR: Detection server did not start. Check that the venv exists and requirements.txt is installed.");
                AddLog("You can also start the server manually: run start_server.bat, then restart this app.");
                ServerStatusText.Text = "Detection server: OFFLINE";
                StatusText.Text       = "Detection server failed — see log.";
                AddLog("WARNING: You can still use 'Test Local Image' to process images if the server is already running.");
                // Don't return — let the user still test local images manually
            }
            else
            {
                ServerStatusText.Text = "Detection server: ready";
                AddLog("Detection server is ready.");
            }

            // ── Step 2: connect to Milestone and subscribe to motion events ──
            if (App.DataModel == null)
            {
                StatusText.Text = "Offline mode — no live events. Use buttons below to test.";
                AddLog("Running in offline mode — live motion events disabled.");
                return;
            }

            AddLog("Looking up motion event type...");
            StatusText.Text = "Looking up motion event type...";

            Guid? motionEventTypeId;
            try
            {
                motionEventTypeId = await MotionEventFinder.FindMotionEventTypeIdAsync(App.DataModel.RestApiClient);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR querying event types: {ex.Message}");
                StatusText.Text = "Error — see log.";
                return;
            }

            if (motionEventTypeId == null)
            {
                AddLog("Could not find a motion event type by name.");
                AddLog("Check the Debug Output window for the full list of event type names exposed by your camera driver.");
                StatusText.Text = "Motion event type not found — check Output window.";
                return;
            }

            AddLog($"Found motion event type ID: {motionEventTypeId}");

            // ── Resolve which cameras to actually monitor ─────────────────────
            var allCameras    = GetAllCameras();
            var targetCameras = FilterTargetCameras(allCameras);

            if (App.Config.TargetCameras != null && App.Config.TargetCameras.Length > 0)
            {
                if (targetCameras.Count == 0)
                {
                    AddLog("ERROR: target_cameras filter matched no cameras. Available cameras on this server:");
                    foreach (var c in allCameras)
                        LogCameraWithChildren(c);
                    StatusText.Text = "No cameras matched target_cameras — check config.json.";
                    return;
                }

                AddLog($"target_cameras filter matched {targetCameras.Count} camera(s):");
                foreach (var c in targetCameras)
                    LogCameraWithChildren(c);
                _targetCameraIds = new HashSet<Guid>(targetCameras.Select(c => c.FQID.ObjectId));
            }
            else
            {
                AddLog($"No target_cameras filter set — monitoring all {allCameras.Count} camera(s) on this server.");
                _targetCameraIds = null;
            }

            StatusText.Text = "Subscribing to motion events...";

            try
            {
                var sourceIds = _targetCameraIds != null
                    ? new SourceIds(_targetCameraIds)
                    : SourceIds.Any;

                var rule = new SubscriptionRule(
                    Modifier.Include,
                    ResourceTypes.Any,
                    sourceIds,
                    new EventTypes(new[] { motionEventTypeId.Value }));

                await App.DataModel.Session.AddSubscriptionAsync(new[] { rule }, default);
                App.DataModel.EventReceiver.EventsReceived += OnEventsReceived;

                AddLog($"Subscribed. Snapshots will be saved to: {SnapshotFolder}");
                AddLog("Monitoring — waiting for motion...");
                StatusText.Text = "Monitoring — waiting for motion...";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR subscribing: {ex.Message}");
                StatusText.Text = "Subscription failed — see log.";
            }
        }

        // -----------------------------------------------------------------------
        // Motion event handler
        // -----------------------------------------------------------------------

        private void OnEventsReceived(object sender, IEnumerable<Event> events)
        {
            foreach (var evt in events)
            {
                _eventCount++;
                CountText.Text = $"Events: {_eventCount}";

                var cameraGuid = ExtractCameraGuid(evt.Source);
                AddLog($"[{evt.Time:HH:mm:ss}] Motion on {evt.Source}");

                if (cameraGuid == null)
                {
                    AddLog($"  Source '{evt.Source}' is not a camera GUID — skipping.");
                    continue;
                }

                var guid      = cameraGuid.Value;
                var timestamp = evt.Time;

                var now  = DateTime.UtcNow;
                var last = _lastTriggered.GetOrAdd(guid, DateTime.MinValue);
                if (now - last < CameraCooldown)
                {
                    var remaining = CameraCooldown - (now - last);
                    AddLog($"  Cooldown active ({remaining.TotalSeconds:F0}s remaining) — skipping.");
                    continue;
                }
                _lastTriggered[guid] = now;

                // Fire and forget on a thread-pool thread (non-blocking for the event thread)
                _ = Task.Run(async () => await GrabSnapshotAsync(guid, timestamp));
            }
        }

        // -----------------------------------------------------------------------
        // Snapshot grab → full async pipeline
        // -----------------------------------------------------------------------

        private async Task GrabSnapshotAsync(Guid cameraGuid, DateTime eventTime)
        {
            try
            {
                var cameraItem = Configuration.Instance.GetItem(cameraGuid, Kind.Camera);
                if (cameraItem == null)
                {
                    Log($"  Camera {cameraGuid:D} not found in configuration — skipping.");
                    return;
                }

                try
                {
                    var camCfg = new VideoOS.Platform.ConfigurationItems.Camera(cameraItem.FQID);
                    Log($"  Camera '{cameraItem.Name}': Enabled={camCfg.Enabled}, RecordingEnabled={camCfg.RecordingEnabled}");
                }
                catch (Exception ex)
                {
                    Log($"  Could not read camera config state: {ex.Message}");
                }

                var safeName  = SanitizeFileName(cameraItem.Name);
                var filename  = $"{safeName}_{eventTime:yyyyMMdd_HHmmss_fff}.jpg";
                var fullPath  = Path.Combine(SnapshotFolder, filename);
                var streamId  = ResolveStreamId(cameraItem);

                var grabResult = await Task.Run(() => SnapshotGrabber.GrabAndSave(cameraItem, fullPath, streamId));

                if (grabResult.Success)
                {
                    await ProcessImageAsync(fullPath, cameraItem.Name, eventTime);
                }
                else if (grabResult.Error != null)
                {
                    Log($"  Snapshot FAILED for camera '{cameraItem.Name}': {grabResult.Error}");
                }
                else
                {
                    Log($"  Snapshot TIMED OUT for camera '{cameraItem.Name}' (no frame within 25 s — camera may not be live/connected)");
                }
            }
            catch (AggregateException aggEx)
            {
                var inner = aggEx.InnerException ?? aggEx;
                Log($"  Snapshot ERROR: {inner.Message}");
            }
            catch (Exception ex)
            {
                Log($"  Snapshot ERROR: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Core processing pipeline: MegaDetector → Classify → Discord
        // -----------------------------------------------------------------------

        private async Task ProcessImageAsync(string fullPath, string cameraName, DateTime eventTime)
        {
            Log($"  Processing: {Path.GetFileName(fullPath)} — running MegaDetector...");

            try
            {
                // ── MegaDetector ─────────────────────────────────────────────
                MegaDetectorResponse mdResult;
                try
                {
                    mdResult = await MegaDetectorClient.AnalyzeImageAsync(fullPath);
                }
                catch (Exception ex)
                {
                    Log($"  MegaDetector ERROR: {ex.Message}");
                    return;
                }

                if (!string.IsNullOrEmpty(mdResult?.error))
                {
                    Log($"  MegaDetector server error: {mdResult.error}");
                    return;
                }

                // Find the best animal detection (class 0 = animal in MegaDetector)
                bool   animalDetected     = false;
                float  highestConfidence  = 0;
                float[] bestBbox          = null;

                if (mdResult?.detections != null)
                {
                    foreach (var det in mdResult.detections)
                    {
                        if (det.class_id == 0 && det.confidence > 0.6f)
                        {
                            animalDetected = true;
                            if (det.confidence > highestConfidence)
                            {
                                highestConfidence = det.confidence;
                                bestBbox          = det.bbox;
                            }
                        }
                    }
                }

                if (!animalDetected)
                {
                    Log($"  No animal detected (false positive / empty frame).");
                    return;
                }

                Log($"  ANIMAL DETECTED! (MegaDetector confidence: {highestConfidence:P1})");
                Log($"  Running SpeciesNet & BioCLIP classification...");

                // ── Classifier ────────────────────────────────────────────────
                ClassificationResponse clsResult;
                try
                {
                    clsResult = await ClassifierClient.ClassifyAnimalAsync(fullPath, bestBbox, App.Config);
                }
                catch (Exception ex)
                {
                    Log($"  Classifier ERROR: {ex.Message}");
                    return;
                }

                if (!string.IsNullOrEmpty(clsResult?.error))
                {
                    Log($"  Classifier server error: {clsResult.error}");
                    return;
                }

                Dispatcher.BeginInvoke(new Action(IncrementAnimalCount));

                string snetGuess = clsResult.speciesnet?.top_species ?? "unknown";
                float  snetConf  = clsResult.speciesnet?.confidence  ?? 0f;
                Log($"  SpeciesNet: {snetGuess} ({snetConf:P1})");

                bool   targetMatch  = clsResult.bioclip?.target_match  ?? false;
                float  targetScore  = clsResult.bioclip?.target_score  ?? 0f;
                string bcTopSpecies = clsResult.bioclip?.top_species   ?? "unknown";
                float  bcTopScore   = clsResult.bioclip?.top_score     ?? 0f;

                // ── Save crop to snapshots folder — always for a target match,
                //    otherwise only if save_all_crops is enabled ─────────────
                byte[] cropBytes = null;
                string cropFile  = null;
                if (!string.IsNullOrEmpty(clsResult.crop_b64) && (targetMatch || App.Config.SaveAllCrops))
                {
                    try
                    {
                        cropBytes = Convert.FromBase64String(clsResult.crop_b64);
                        cropFile  = $"crop_{Path.GetFileName(fullPath)}";
                        var cropPath = Path.Combine(SnapshotFolder, cropFile);
                        Directory.CreateDirectory(SnapshotFolder);
                        File.WriteAllBytes(cropPath, cropBytes);
                        Log($"  Crop saved: {cropPath}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  WARNING: Could not decode/save crop: {ex.Message}");
                    }
                }

                if (targetMatch)
                {
                    Log($"  TARGET SPECIES MATCHED! BioCLIP score: {targetScore:P1}");
                    Dispatcher.BeginInvoke(new Action(IncrementTargetCount));
                }
                else
                {
                    Log($"  Target species not matched. BioCLIP top: {bcTopSpecies} ({bcTopScore:P1})");
                }

                // ── Discord alert — always for a target match; for non-matches
                //    only if discord_notify_non_target is enabled ─────────────
                bool shouldNotify = targetMatch || App.Config.NotifyNonTargetMatches;
                if (shouldNotify && !string.IsNullOrEmpty(App.Config.DiscordWebhookUrl) && cropBytes != null)
                {
                    Log("  Sending Discord alert...");
                    bool discordOk = await DiscordClient.PostAlertAsync(
                        webhookUrl:         App.Config.DiscordWebhookUrl,
                        cameraName:         cameraName,
                        timestamp:          eventTime,
                        targetSpecies:      App.Config.TargetSpecies,
                        isTargetMatch:      targetMatch,
                        snetGuess:          snetGuess,
                        snetConfidence:     snetConf,
                        bioclipTopSpecies:  bcTopSpecies,
                        bioclipTopScore:    bcTopScore,
                        bioclipTargetScore: targetScore,
                        cropImageBytes:     cropBytes,
                        cropFileName:       cropFile ?? "crop.jpg");

                    Log(discordOk
                        ? "  Discord alert sent successfully!"
                        : "  WARNING: Discord post failed — check webhook URL and connectivity.");
                }
            }
            catch (AggregateException aggEx)
            {
                var inner = aggEx.InnerException ?? aggEx;
                Log($"  Processing ERROR: {inner.Message}");
            }
            catch (Exception ex)
            {
                Log($"  Processing ERROR: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Manual test buttons
        // -----------------------------------------------------------------------

        private void TestLocalImageBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                AddLog($"Manual test: {dlg.FileName}");
                _ = Task.Run(async () =>
                    await ProcessImageAsync(dlg.FileName, "Manual Test", DateTime.UtcNow));
            }
        }

        private void TestSnapshotBtn_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Manual snapshot triggered...");
            TriggerSnapshotsOnAllCameras();
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            AddLog("Auto-poll triggered...");
            TriggerSnapshotsOnAllCameras();
        }

        private void AutoPollCheckbox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (AutoPollCheckbox.IsChecked == true)
            {
                _pollTimer.Start();
                AddLog("Auto-polling started (10 s interval).");
            }
            else
            {
                _pollTimer.Stop();
                AddLog("Auto-polling stopped.");
            }
        }

        // -----------------------------------------------------------------------
        // Camera enumeration helpers
        // -----------------------------------------------------------------------

        private void TriggerSnapshotsOnAllCameras()
        {
            var cameras = _targetCameraIds != null
                ? GetAllCameras().Where(c => _targetCameraIds.Contains(c.FQID.ObjectId)).ToList()
                : GetAllCameras();

            if (cameras.Count == 0)
            {
                AddLog("No cameras found in configuration.");
                return;
            }
            foreach (var cam in cameras)
            {
                var guid      = cam.FQID.ObjectId;
                var timestamp = DateTime.UtcNow;
                _ = Task.Run(async () => await GrabSnapshotAsync(guid, timestamp));
            }
        }

        /// <summary>
        /// Logs a camera plus its child items (name + Kind + ObjectId), one level deep.
        /// Some devices (e.g. a multi-channel virtual/software camera) expose their
        /// actual video sources as children of a single Camera item rather than as
        /// separate top-level cameras — this makes that structure visible in the log.
        /// </summary>
        private void LogCameraWithChildren(Item cam)
        {
            AddLog($"    - {cam.Name}");
            List<Item> children = null;
            try { children = cam.GetChildren(); } catch (Exception ex) { AddLog($"        (could not read children: {ex.Message})"); }

            if (children == null || children.Count == 0)
                return;

            foreach (var child in children)
                AddLog($"        · child: '{child.Name}' (Kind={child.FQID?.Kind}, Id={child.FQID?.ObjectId})");
        }

        /// <summary>Filters cameras by config.json's "target_cameras" name substrings (case-insensitive).</summary>
        private static List<Item> FilterTargetCameras(List<Item> allCameras)
        {
            var filters = App.Config.TargetCameras;
            if (filters == null || filters.Length == 0)
                return allCameras;

            return allCameras.Where(c => filters.Any(f =>
                !string.IsNullOrWhiteSpace(f) &&
                c.Name != null &&
                c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        private List<Item> GetAllCameras(Item parentItem = null)
        {
            var cameras = new List<Item>();
            try
            {
                var children = parentItem == null
                    ? Configuration.Instance.GetItems()
                    : parentItem.GetChildren();

                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child.FQID.Kind == Kind.Camera)
                            cameras.Add(child);
                        cameras.AddRange(GetAllCameras(child));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllCameras error: {ex.Message}");
            }
            return cameras;
        }

        // -----------------------------------------------------------------------
        // UI helpers (all callable from any thread)
        // -----------------------------------------------------------------------

        /// <summary>Thread-safe log append + auto-scroll.</summary>
        private void Log(string message) =>
            Dispatcher.BeginInvoke(new Action(() => AddLog(message)));

        private void AddLog(string message)
        {
            _log.Add(message);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }

        private void IncrementAnimalCount()
        {
            _animalCount++;
            AnimalCountText.Text = $"Animals: {_animalCount}";
        }

        private void IncrementTargetCount()
        {
            _targetCount++;
            TargetCountText.Text = $"Targets: {_targetCount}";
        }

        // -----------------------------------------------------------------------
        // Static helpers
        // -----------------------------------------------------------------------

        private static Guid? ExtractCameraGuid(string source)
        {
            if (string.IsNullOrEmpty(source))
                return null;

            const string prefix = "cameras/";
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var after  = source.Substring(prefix.Length);
            var slash  = after.IndexOf('/');
            var guidStr = slash >= 0 ? after.Substring(0, slash) : after;

            return Guid.TryParse(guidStr, out var g) ? g : (Guid?)null;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
