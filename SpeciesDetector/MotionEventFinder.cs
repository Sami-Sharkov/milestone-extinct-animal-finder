using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SpeciesDetector
{
    static class MotionEventFinder
    {
        public static async Task<Guid?> FindMotionEventTypeIdAsync(CachedRestApiClient restApiClient)
        {
            var eventTypesJson = await restApiClient.LookupResourceAsync("eventTypes/");
            var eventTypes = eventTypesJson.GetChild("array").GetChildren();

            foreach (var eventType in eventTypes)
            {
                var name = eventType.GetString("displayName");
                var id = eventType.GetChild("relations")?.GetChild("self")?.GetString("id");

                Debug.WriteLine($"EventType: {name} -> {id}");

                if (name != null &&
                    name.IndexOf("motion", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    name.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (Guid.TryParse(id, out var guid))
                        return guid;
                }
            }

            return null;
        }
    }
}