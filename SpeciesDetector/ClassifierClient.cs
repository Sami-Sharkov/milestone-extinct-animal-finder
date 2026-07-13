using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;

namespace SpeciesDetector
{
    public class SpeciesNetResult
    {
        public string top_species { get; set; }
        public float confidence { get; set; }
    }

    public class BioClipResult
    {
        public bool target_match { get; set; }
        public float target_score { get; set; }
        public string top_species { get; set; }
        public float top_score { get; set; }
    }

    public class ClassificationResponse
    {
        public string status { get; set; }
        public string error { get; set; }
        public string crop_path { get; set; }
        public SpeciesNetResult speciesnet { get; set; }
        public BioClipResult bioclip { get; set; }
        public bool discord_sent { get; set; }
    }

    public static class ClassifierClient
    {
        private static readonly string PythonExePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\venv\Scripts\python.exe"));
        private static readonly string ScriptPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\classifier_sidecar.py"));
        private static readonly string ConfigPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\config.json"));

        public static async Task<ClassificationResponse> ClassifyAnimalAsync(string imagePath, float[] bbox)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found", imagePath);
                
            if (bbox == null || bbox.Length != 4)
                throw new ArgumentException("bbox must have exactly 4 elements (xmin, ymin, xmax, ymax)");

            // Invariant culture handles decimal parsing smoothly (e.g. 0.5 vs 0,5)
            var xmin = bbox[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ymin = bbox[1].ToString(System.Globalization.CultureInfo.InvariantCulture);
            var xmax = bbox[2].ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ymax = bbox[3].ToString(System.Globalization.CultureInfo.InvariantCulture);

            var startInfo = new ProcessStartInfo
            {
                FileName = PythonExePath,
                Arguments = $"\"{ScriptPath}\" \"{imagePath}\" \"{ConfigPath}\" {xmin} {ymin} {xmax} {ymax}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("Failed to start python process");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await Task.Run(() => process.WaitForExit());

                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    System.Diagnostics.Debug.WriteLine($"[Classifier Stderr]: {stderr}");
                }

                var stdout = await stdoutTask;
                
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<ClassificationResponse>(stdout, options);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to parse Classifier output: '{stdout}'", ex);
                }
            }
        }
    }
}
