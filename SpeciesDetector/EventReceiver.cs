using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VideoOS.Platform.EventsAndState;

namespace SpeciesDetector
{
    class EventReceiver : IEventReceiver
    {
        public event EventHandler<ConnectionState> ConnectionStateChanged;
        public event EventHandler<IEnumerable<Event>> EventsReceived;

        public async Task OnConnectionStateChangedAsync(ConnectionState newState)
        {
            await App.Current.Dispatcher.BeginInvoke(new Action(() => ConnectionStateChanged?.Invoke(this, newState)));
        }

        public async Task OnEventsReceivedAsync(IEnumerable<Event> events)
        {
            await App.Current.Dispatcher.BeginInvoke(new Action(() => EventsReceived?.Invoke(this, events)));
        }
    }
}