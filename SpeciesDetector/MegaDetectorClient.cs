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

            // Write output to a temp file instead of using stdio pipes.
            // Pipe-based redirection (UseShellExecute=false) causes Windows to inherit all
            // open parent handles into the child process. The MIP SDK opens many handles,
            // which can exhaust system resources and make Process.Start throw
            // "Not enough system resources to perform this operation".
            var outFile = Path.GetTempFileName();
            try
            {
                // Wrap the script call in a small cmd snippet that redirects stdout to the
            // temp file, keeping stderr on the console (or discarded with CreateNoWindow).
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{PythonExePath}\" \"{ScriptPath}\" \"{imagePath}\" > \"{outFile}\" 2>nul\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("Failed to start python process");

                    await Task.Run(() => process.WaitForExit());
                    System.Diagnostics.Debug.WriteLine($"[MegaDetector] Process exited with code {process.ExitCode}");
                }

                var stdout = File.ReadAllText(outFile);
                System.Diagnostics.Debug.WriteLine($"[MegaDetector Stdout]: {stdout}");

                // YOLOv5 writes banner lines ("Fusing layers...", "Model summary:", etc.)
                // directly to the OS-level fd1, bypassing our sys.stdout redirect.
                // Those lines end up in the temp file before the JSON.
                // The JSON is always the final print(), so grab the last non-empty line.
                string jsonLine = null;
                foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                        jsonLine = trimmed;
                }

                if (jsonLine == null)
                    throw new InvalidOperationException($"Failed to parse MegaDetector output — no JSON line found. Raw output: '{stdout}'");

                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<MegaDetectorResponse>(jsonLine, options);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to parse MegaDetector output: '{jsonLine}' (raw: '{stdout}')", ex);
                }
            }
            finally
            {
                try { File.Delete(outFile); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
