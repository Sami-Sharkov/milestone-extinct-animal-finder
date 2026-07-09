using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using VideoOS.Platform.EventsAndState;

namespace SpeciesDetector
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<string> _log = new ObservableCollection<string>();
        private int _eventCount = 0;

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
            StatusText.Text = $"Subscribing to motion events...";

            try
            {
                var rule = new SubscriptionRule(
                    Modifier.Include,
                    ResourceTypes.Any,
                    SourceIds.Any,
                    new EventTypes(new[] { motionEventTypeId.Value }));

                await App.DataModel.Session.AddSubscriptionAsync(new[] { rule }, default);
                App.DataModel.EventReceiver.EventsReceived += OnEventsReceived;

                AddLog("Subscribed. Waiting for motion events...");
                StatusText.Text = "Monitoring — waiting for motion...";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR subscribing: {ex.Message}");
                StatusText.Text = "Subscription failed — see log.";
            }
        }

        private void OnEventsReceived(object sender, IEnumerable<Event> events)
        {
            foreach (var evt in events)
            {
                _eventCount++;
                AddLog($"[{evt.Time:HH:mm:ss}] Motion on {evt.Source}");
                CountText.Text = $"Events received: {_eventCount}";
            }
        }

        private void AddLog(string message)
        {
            _log.Add(message);
            // Auto-scroll to the bottom
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }
    }
}
