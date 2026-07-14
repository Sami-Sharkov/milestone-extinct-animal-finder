using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeciesDetector
{
    /// <summary>
    /// Typed wrapper around config.json.
    /// Loaded once at startup via <see cref="Load"/>.
    /// </summary>
    public class AppConfig
    {
        [JsonPropertyName("target_species")]
        public string TargetSpecies { get; set; } = "Unknown Species";

        [JsonPropertyName("candidate_species")]
        public string[] CandidateSpecies { get; set; } = Array.Empty<string>();

        [JsonPropertyName("country_code")]
        public string CountryCode { get; set; }

        [JsonPropertyName("confidence_threshold")]
        public float ConfidenceThreshold { get; set; } = 0.5f;

        [JsonPropertyName("discord_webhook_url")]
        public string DiscordWebhookUrl { get; set; }

        /// <summary>
        /// If true, also posts a Discord alert for animals that were detected and
        /// classified but did NOT match the target species (useful while tuning
        /// thresholds / verifying the pipeline). Target matches always notify
        /// regardless of this setting.
        /// </summary>
        [JsonPropertyName("discord_notify_non_target")]
        public bool NotifyNonTargetMatches { get; set; } = false;

        [JsonPropertyName("save_all_crops")]
        public bool SaveAllCrops { get; set; } = true;

        /// <summary>
        /// Which camera stream to grab snapshots from — either a 1-based ordinal
        /// ("2" = second stream defined on the camera) or a substring to match
        /// against the stream's display name (e.g. "high"). Empty/null means
        /// use the camera's default live stream (SDK default behaviour).
        /// </summary>
        [JsonPropertyName("camera_stream")]
        public string CameraStream { get; set; }

        /// <summary>Seconds between Auto Poll snapshot grabs when the checkbox is checked.</summary>
        [JsonPropertyName("poll_interval_seconds")]
        public int PollIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Case-insensitive substrings matched against camera names — only cameras
        /// whose name contains one of these are monitored/polled. Empty/null means
        /// monitor every camera on the server (only sensible on a server dedicated
        /// to this project — on a shared test server, set this to avoid picking up
        /// other people's cameras).
        /// </summary>
        [JsonPropertyName("target_cameras")]
        public string[] TargetCameras { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Loads config.json from the given path.
        /// Returns a default instance if the file is missing or invalid.
        /// </summary>
        public static AppConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"[AppConfig] File not found: {path}. Using defaults.");
                return new AppConfig();
            }

            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppConfig] Failed to parse: {ex.Message}. Using defaults.");
                return new AppConfig();
            }
        }
    }
}
