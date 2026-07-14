using System;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;

namespace SpeciesDetector
{
    /// <summary>
    /// Resolves a camera's stream selector (from config.json's "camera_stream")
    /// to a concrete stream Guid that <see cref="VideoOS.Platform.Live.JPEGLiveSource.StreamId"/>
    /// accepts, so snapshots can be pulled from a specific stream (e.g. a test
    /// rig feeding footage into stream 2 instead of the camera's default stream 1).
    /// </summary>
    static class CameraStreamResolver
    {
        /// <summary>
        /// Returns the stream Guid matching <paramref name="streamSelector"/>, or null
        /// if the selector is empty or no match is found (caller should fall back to
        /// the SDK's default live stream in that case).
        /// </summary>
        public static Guid? Resolve(Item cameraItem, string streamSelector, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(streamSelector))
                return null;

            try
            {
                var camera = new Camera(cameraItem.FQID);
                var streams = camera.StreamFolder?.Streams?.ToList();

                if (streams == null || streams.Count == 0)
                {
                    log($"  No streams found for camera '{cameraItem.Name}' via config API — using default live stream.");
                    return null;
                }

                log($"  Camera '{cameraItem.Name}' streams: " +
                    string.Join(", ", streams.Select(s => $"\"{s.DisplayName ?? s.Name}\"")));

                // Prefer a name match (XProtect's default stream names are
                // "Video stream 1", "Video stream 2", ... so this also catches
                // a plain numeric selector like "2").
                var byName = streams.FirstOrDefault(s =>
                    (s.DisplayName ?? s.Name ?? string.Empty)
                        .IndexOf(streamSelector, StringComparison.OrdinalIgnoreCase) >= 0);
                if (byName != null)
                {
                    log($"  Using stream \"{byName.DisplayName ?? byName.Name}\" for camera '{cameraItem.Name}'.");
                    return byName.Guid;
                }

                // Fall back to treating the selector as a 1-based ordinal position.
                if (int.TryParse(streamSelector, out int ordinal) && ordinal >= 1 && ordinal <= streams.Count)
                {
                    var byOrdinal = streams[ordinal - 1];
                    log($"  Using stream #{ordinal} (\"{byOrdinal.DisplayName ?? byOrdinal.Name}\") for camera '{cameraItem.Name}'.");
                    return byOrdinal.Guid;
                }

                log($"  WARNING: camera_stream '{streamSelector}' matched no stream on '{cameraItem.Name}' — using default live stream.");
                return null;
            }
            catch (Exception ex)
            {
                log($"  WARNING: Could not resolve camera_stream '{streamSelector}' for '{cameraItem.Name}': {ex.Message}. Using default live stream.");
                return null;
            }
        }
    }
}
