using System;
using VideoOS.Platform.Login;
using VideoOS.Platform.EventsAndState;

namespace SpeciesDetector
{
    class DataModel : IDisposable
    {
        public EventReceiver EventReceiver { get; } = new EventReceiver();
        public IEventsAndStateSession Session { get; }
        public CachedRestApiClient RestApiClient { get; }

        public DataModel(LoginSettings loginSettings)
        {
            Session = EventsAndStateSession.Create(loginSettings, EventReceiver);
            RestApiClient = new CachedRestApiClient(loginSettings);
        }

        public void Dispose()
        {
            Session?.Dispose();
            RestApiClient?.Dispose();
        }
    }
}