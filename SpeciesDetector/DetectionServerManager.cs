using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpeciesDetector
{
    /// <summary>
    /// Manages the lifecycle of the Python detection server (detection_server.py).
    /// Auto-starts the server if it isn't already running, polls until ready,
    /// and optionally kills it when the host app exits.
    /// </summary>
    public static class DetectionServerManager
    {
        private const string HealthUrl  = "http://127.0.0.1:5050/health";
        private const int    PollMs     = 2_000;   // poll every 2 seconds
        private const int    TimeoutSec = 240;     // up to 4 min — BioCLIP is now warmed up eagerly at startup too

        // Resolved once, based on the exe's location (…\bin\Debug\net4.7.2\)
        private static readonly string VenvPython = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\venv\Scripts\python.exe"));

        private static readonly string ServerScript = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\detection_server.py"));

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private static Process _serverProcess;

        /// <summary>
        /// Checks if the server is already responding; if not, starts it and waits until ready.
        /// </summary>
        /// <param name="logCallback">Called on the UI thread to report progress.</param>
        /// <returns>True if the server is ready; false if timed out or failed to start.</returns>
        public static async Task<bool> EnsureRunningAsync(Action<string> logCallback)
        {
            // Fast path — server already up (e.g. started manually by the user)
            if (await IsHealthyAsync().ConfigureAwait(false))
            {
                logCallback("  Detection server already running — skipping auto-start.");
                return true;
            }

            // Verify we can find the venv and script
            if (!File.Exists(VenvPython))
            {
                logCallback($"  ERROR: Python venv not found at: {VenvPython}");
                logCallback("  Please create it: python -m venv venv && pip install -r requirements.txt");
                return false;
            }
            if (!File.Exists(ServerScript))
            {
                logCallback($"  ERROR: detection_server.py not found at: {ServerScript}");
                return false;
            }

            logCallback("  Starting detection server (loading ML models, incl. BioCLIP warmup — may take ~2 min the first time)…");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = VenvPython,
                    Arguments              = $"\"{ServerScript}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = false,  // let it write to its own console/log
                    RedirectStandardError  = false,
                };
                _serverProcess = Process.Start(psi);
                if (_serverProcess == null)
                {
                    logCallback("  ERROR: Process.Start returned null — could not launch server.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logCallback($"  ERROR launching server: {ex.Message}");
                return false;
            }

            // Poll /health until ready or timeout
            var deadline = DateTime.UtcNow.AddSeconds(TimeoutSec);
            int dots     = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollMs).ConfigureAwait(false);

                if (_serverProcess.HasExited)
                {
                    logCallback($"  ERROR: Server process exited prematurely (code {_serverProcess.ExitCode}).");
                    return false;
                }

                if (await IsHealthyAsync().ConfigureAwait(false))
                {
                    logCallback("  ✓ Detection server is ready.");
                    return true;
                }

                dots++;
                if (dots % 5 == 0)   // log progress every 10 s
                    logCallback($"  Still loading models… ({dots * PollMs / 1000} s elapsed)");
            }

            logCallback("  TIMEOUT: Server did not become ready within 120 s.");
            return false;
        }

        /// <summary>
        /// Stops the server process that was auto-started by this manager, if any.
        /// Call from Application_Exit.
        /// </summary>
        public static void Shutdown()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(3_000);
                    Debug.WriteLine("[DetectionServerManager] Server process killed on exit.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DetectionServerManager] Kill failed: {ex.Message}");
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static async Task<bool> IsHealthyAsync()
        {
            try
            {
                var raw     = await _http.GetStringAsync(HealthUrl).ConfigureAwait(false);
                var json    = JsonSerializer.Deserialize<JsonElement>(raw);
                var status  = json.GetProperty("status").GetString();
                return status == "ready";
            }
            catch
            {
                return false;
            }
        }
    }
}
