using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeciesDetector
{
    public class DetectionLogEntry
    {
        [JsonPropertyName("timestamp")]                public string Timestamp { get; set; }
        [JsonPropertyName("camera")]                   public string Camera { get; set; }
        [JsonPropertyName("image_file")]                public string ImageFile { get; set; }
        [JsonPropertyName("crop_file")]                 public string CropFile { get; set; }
        [JsonPropertyName("megadetector_confidence")]   public float MegaDetectorConfidence { get; set; }
        [JsonPropertyName("speciesnet_guess")]          public string SpeciesNetGuess { get; set; }
        [JsonPropertyName("speciesnet_confidence")]     public float SpeciesNetConfidence { get; set; }
        [JsonPropertyName("bioclip_top_species")]       public string BioClipTopSpecies { get; set; }
        [JsonPropertyName("bioclip_top_score")]         public float BioClipTopScore { get; set; }
        [JsonPropertyName("bioclip_target_score")]      public float BioClipTargetScore { get; set; }
        [JsonPropertyName("target_species")]            public string TargetSpecies { get; set; }
        [JsonPropertyName("target_match")]              public bool TargetMatch { get; set; }
    }

    /// <summary>
    /// Appends one JSON line per classified animal to snapshots/detections_log.jsonl —
    /// a structured record of every detection (not just target matches), meant as the
    /// dataset groundwork for later training a custom classifier once enough real
    /// detections have been collected.
    /// </summary>
    public static class DetectionLogger
    {
        private static readonly object _lock = new object();

        public static void Append(string folder, DetectionLogEntry entry)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "detections_log.jsonl");
                var json = JsonSerializer.Serialize(entry);
                lock (_lock)
                {
                    File.AppendAllText(path, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DetectionLogger] Append failed: {ex.Message}");
            }
        }
    }
}
