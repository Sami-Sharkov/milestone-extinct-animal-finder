using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        private int _eventCount = 0;
        private DispatcherTimer _pollTimer;

        // Serialize classifier calls — SpeciesNet caches models to disk and crashes
        // if two processes try to initialize simultaneously from the same venv.
        private static readonly System.Threading.SemaphoreSlim _classifierSemaphore =
            new System.Threading.SemaphoreSlim(1, 1);

        // Snapshots land here, relative to wherever the exe runs from.
        private static readonly string SnapshotFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _log;
            
            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(10);
            _pollTimer.Tick += PollTimer_Tick;

            if (App.DataModel != null)
            {
                Loaded += MainWindow_Loaded;
            }
            else
            {
                StatusText.Text = "Not connected — DataModel is null.";
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("Connected to Milestone. Looking up motion event type...");
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
                AddLog("Check the Debug Output window (View → Output → Debug) for the full list of event type names your camera driver exposes, and report them back.");
                StatusText.Text = "Motion event type not found — check Output window.";
                return;
            }

            AddLog($"Found motion event type ID: {motionEventTypeId}");
            StatusText.Text = "Subscribing to motion events...";

            try
            {
                var rule = new SubscriptionRule(
                    Modifier.Include,
                    ResourceTypes.Any,
                    SourceIds.Any,
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
        // Motion event → snapshot
        // -----------------------------------------------------------------------

        private void OnEventsReceived(object sender, IEnumerable<Event> events)
        {
            foreach (var evt in events)
            {
                _eventCount++;
                CountText.Text = $"Events received: {_eventCount}";

                // evt.Source is a REST resource path, e.g. "cameras/3fa85f64-..."
                // We need the GUID part to look up the Item.
                var cameraGuid = ExtractCameraGuid(evt.Source);

                string cameraLabel = cameraGuid.HasValue
                    ? cameraGuid.Value.ToString("D").Substring(0, 8) + "…"
                    : evt.Source;

                AddLog($"[{evt.Time:HH:mm:ss}] Motion on {evt.Source}");

                if (cameraGuid == null)
                {
                    AddLog($"  ↳ Source '{evt.Source}' is not a camera — skipping snapshot.");
                    continue;
                }

                // Kick off snapshot grab on a background thread so the UI doesn't freeze.
                // We capture the values we need before the lambda runs.
                var guid      = cameraGuid.Value;
                var timestamp = evt.Time;
                Task.Run(() => GrabSnapshot(guid, timestamp));
            }
        }

        /// <summary>
        /// Parses the GUID out of a source path like "cameras/{guid}" or
        /// "cameras/{guid}/streams/{stream-guid}".
        /// Returns null if the source is not a camera resource.
        /// </summary>
        private static Guid? ExtractCameraGuid(string source)
        {
            if (string.IsNullOrEmpty(source))
                return null;

            // REST resource path format: "cameras/<guid>" or "cameras/<guid>/..."
            const string prefix = "cameras/";
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            // Take just the segment right after "cameras/"
            var after = source.Substring(prefix.Length);
            var slash = after.IndexOf('/');
            var guidStr = slash >= 0 ? after.Substring(0, slash) : after;

            return Guid.TryParse(guidStr, out var g) ? g : (Guid?)null;
        }

        /// <summary>
        /// Looks up the camera Item by GUID, grabs one JPEG frame, saves it,
        /// and logs the result back on the UI thread.
        /// Must be called on a background thread (blocks while waiting for frame).
        /// </summary>
        private void GrabSnapshot(Guid cameraGuid, DateTime eventTime)
        {
            string logLine;
            try
            {
                // Configuration.Instance.GetItem() is the standard SDK call to resolve
                // a GUID + Kind to an Item — confirmed pattern from ConfigApiClient sample.
                var cameraItem = Configuration.Instance.GetItem(cameraGuid, Kind.Camera);
                if (cameraItem == null)
                {
                    logLine = $"  ↳ Camera {cameraGuid:D} not found in configuration — skipping.";
                    Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                    return;
                }

                // Build a filename: "<camera-name>_<timestamp>.jpg"
                // Sanitize the camera name for use as a filename.
                var safeName = SanitizeFileName(cameraItem.Name);
                var filename = $"{safeName}_{eventTime:yyyyMMdd_HHmmss_fff}.jpg";
                var fullPath = Path.Combine(SnapshotFolder, filename);

                bool saved = SnapshotGrabber.GrabAndSave(cameraItem, fullPath);

                if (saved)
                {
                    ProcessImageFile(fullPath);
                }
                else
                {
                    logLine = $"  ↳ Snapshot TIMED OUT for camera '{cameraItem.Name}' (no frame within 10 s)";
                    Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                }
            }
            catch (AggregateException aggEx)
            {
                // Unwrap AggregateException so we see the real inner error, not
                // the generic "One or more errors occurred" wrapper message.
                var inner = aggEx.InnerException ?? aggEx;
                logLine = $"  ↳ Snapshot ERROR: {inner.Message}";
                Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
            }
            catch (Exception ex)
            {
                logLine = $"  ↳ Snapshot ERROR: {ex.Message}";
                Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
            }
        }

        private void ProcessImageFile(string fullPath)
        {
            string logLine = $"  ↳ Processing image: {fullPath}. Running MegaDetector...";
            Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));

            try
            {
                var mdTask = MegaDetectorClient.AnalyzeImageAsync(fullPath);
                mdTask.Wait(); // Safe to block here, we are on a background Task thread
                var mdResult = mdTask.Result;

                if (mdResult.error != null)
                {
                    logLine = $"  ↳ MegaDetector ERROR: {mdResult.error}";
                    Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                }
                else
                {
                    bool animalDetected = false;
                    float highestConfidence = 0;
                    float[] bestBbox = null;

                    if (mdResult.detections != null)
                    {
                        foreach (var det in mdResult.detections)
                        {
                            // In typical YOLOv5 0-indexed, Class 0 is Animal (for MegaDetector)
                            if (det.class_id == 0 && det.confidence > 0.6f)
                            {
                                animalDetected = true;
                                if (det.confidence > highestConfidence)
                                {
                                    highestConfidence = det.confidence;
                                    bestBbox = det.bbox;
                                }
                            }
                        }
                    }

                    if (animalDetected)
                    {
                        logLine = $"  ★★★ ANIMAL DETECTED! (Confidence: {highestConfidence:P1}) ★★★";
                        Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                        
                        logLine = "  ↳ Running SpeciesNet & BioCLIP classification...";
                        Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));

                        // Serialize: only one SpeciesNet process at a time to avoid
                        // model-cache file conflicts that cause cryptic pygments errors.
                        _classifierSemaphore.Wait();
                        ClassificationResponse clsResult;
                        try
                        {
                            var classifyTask = ClassifierClient.ClassifyAnimalAsync(fullPath, bestBbox);
                            classifyTask.Wait();
                            clsResult = classifyTask.Result;
                        }
                        finally
                        {
                            _classifierSemaphore.Release();
                        }

                        if (clsResult.error != null)
                        {
                            logLine = $"  ↳ Classifier ERROR: {clsResult.error}";
                            Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                        }
                        else
                        {
                            string snetGuess = clsResult.speciesnet?.top_species ?? "Unknown";
                            logLine = $"  ↳ SpeciesNet guess: {snetGuess} ({clsResult.speciesnet?.confidence ?? 0:P1})";
                            Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));

                            if (clsResult.bioclip?.target_match == true)
                            {
                                logLine = $"  🎯 TARGET SPECIES DETECTED! Score: {clsResult.bioclip.target_score:P1}";
                                if (clsResult.discord_sent)
                                    logLine += " [Discord Alert Sent]";
                                
                                // Update counter on UI thread
                                Dispatcher.BeginInvoke(new Action(() => IncrementTargetCount()));
                            }
                            else
                            {
                                logLine = $"  ↳ Target species not matched. Top BioCLIP guess: {clsResult.bioclip?.top_species} ({clsResult.bioclip?.top_score ?? 0:P1})";
                            }
                            
                            Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                            Dispatcher.BeginInvoke(new Action(() => IncrementAnimalCount()));
                        }
                    }
                    else
                    {
                        logLine = $"  ↳ No animal detected (False positive / empty frame).";
                        Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
                    }
                }
            }
            catch (AggregateException aggEx)
            {
                var inner = aggEx.InnerException ?? aggEx;
                logLine = $"  ↳ Processing ERROR: {inner.Message}";
                Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
            }
            catch (Exception ex)
            {
                logLine = $"  ↳ Processing ERROR: {ex.Message}";
                Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private int _animalCount = 0;
        private int _targetCount = 0;

        private void IncrementAnimalCount()
        {
            _animalCount++;
            AnimalCountText.Text = $"Animals detected: {_animalCount}";
        }

        private void IncrementTargetCount()
        {
            _targetCount++;
            TargetCountText.Text = $"Targets matched: {_targetCount}";
        }

        private void AddLog(string message)
        {
            _log.Add(message);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private void AutoPollCheckbox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (AutoPollCheckbox.IsChecked == true)
            {
                _pollTimer.Start();
                AddLog("Auto-polling started (10s interval).");
            }
            else
            {
                _pollTimer.Stop();
                AddLog("Auto-polling stopped.");
            }
        }

        private void TestLocalImageBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                var fullPath = dlg.FileName;
                AddLog($"Manual test triggered for local image: {fullPath}");
                Task.Run(() => ProcessImageFile(fullPath));
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

        private void TriggerSnapshotsOnAllCameras()
        {
            var cameras = GetAllCameras();
            if (cameras.Count == 0)
            {
                AddLog("No cameras found in configuration.");
                return;
            }

            foreach (var cam in cameras)
            {
                var guid = cam.FQID.ObjectId;
                var timestamp = DateTime.UtcNow;
                Task.Run(() => GrabSnapshot(guid, timestamp));
            }
        }

        private List<Item> GetAllCameras(Item parentItem = null)
        {
            var cameras = new List<Item>();
            try
            {
                var children = parentItem == null ? Configuration.Instance.GetItems() : parentItem.GetChildren();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child.FQID.Kind == Kind.Camera)
                        {
                            cameras.Add(child);
                        }
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
    }
}
