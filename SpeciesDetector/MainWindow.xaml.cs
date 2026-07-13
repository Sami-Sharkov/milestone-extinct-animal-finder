using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using VideoOS.Platform;
using VideoOS.Platform.EventsAndState;

namespace SpeciesDetector
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<string> _log = new ObservableCollection<string>();
        private int _eventCount = 0;

        // Snapshots land here, relative to wherever the exe runs from.
        private static readonly string SnapshotFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _log;

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
                    logLine = $"  ↳ Snapshot saved: {fullPath}. Running MegaDetector...";
                    Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));

                    // Call Python sidecar
                    var mdTask = MegaDetectorClient.AnalyzeImageAsync(fullPath);
                    mdTask.Wait(); // Safe to block here, we are on a background Task thread
                    var mdResult = mdTask.Result;

                    if (mdResult.error != null)
                    {
                        logLine = $"  ↳ MegaDetector ERROR: {mdResult.error}";
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

                            var classifyTask = ClassifierClient.ClassifyAnimalAsync(fullPath, bestBbox);
                            classifyTask.Wait();
                            var clsResult = classifyTask.Result;

                            if (clsResult.error != null)
                            {
                                logLine = $"  ↳ Classifier ERROR: {clsResult.error}";
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
                                
                                Dispatcher.BeginInvoke(new Action(() => IncrementAnimalCount()));
                            }
                        }
                        else
                        {
                            logLine = $"  ↳ No animal detected (False positive / empty frame).";
                        }
                    }
                }
                else
                {
                    logLine = $"  ↳ Snapshot TIMED OUT for camera '{cameraItem.Name}' (no frame within 10 s)";
                }
            }
            catch (Exception ex)
            {
                logLine = $"  ↳ Snapshot ERROR: {ex.Message}";
            }

            // Marshal back to UI thread for the log list
            Dispatcher.BeginInvoke(new Action(() => AddLog(logLine)));
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
    }
}
