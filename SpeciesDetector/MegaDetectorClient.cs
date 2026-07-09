using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;

namespace SpeciesDetector
{
    public class DetectionResult
    {
        public int class_id { get; set; }
        public string name { get; set; }
        public float confidence { get; set; }
        public float[] bbox { get; set; }
    }

    public class MegaDetectorResponse
    {
        public string status { get; set; }
        public string error { get; set; }
        public DetectionResult[] detections { get; set; }
    }

    public static class MegaDetectorClient
    {
        // Assume the venv is created in the project root folder.
        // We resolve paths relative to AppDomain.CurrentDomain.BaseDirectory (which is usually bin/Debug/net4.7.2/)
        private static readonly string PythonExePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\venv\Scripts\python.exe"));
        private static readonly string ScriptPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\megadetector_sidecar.py"));

        public static async Task<MegaDetectorResponse> AnalyzeImageAsync(string imagePath)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found", imagePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = PythonExePath,
                Arguments = $"\"{ScriptPath}\" \"{imagePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("Failed to start python process");

                // Read stdout and stderr
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await Task.Run(() => process.WaitForExit());

                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    System.Diagnostics.Debug.WriteLine($"[MegaDetector Stderr]: {stderr}");
                }

                var stdout = await stdoutTask;
                
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<MegaDetectorResponse>(stdout, options);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to parse MegaDetector output: '{stdout}'", ex);
                }
            }
        }
    }
}
