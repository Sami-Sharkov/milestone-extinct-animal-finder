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
