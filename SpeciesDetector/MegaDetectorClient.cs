using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpeciesDetector
{
    public class DetectionResult
    {
        public int     class_id   { get; set; }
        public string  name       { get; set; }
        public float   confidence { get; set; }
        public float[] bbox       { get; set; }
    }

    public class MegaDetectorResponse
    {
        public string            status     { get; set; }
        public string            error      { get; set; }
        public DetectionResult[] detections { get; set; }
    }

    /// <summary>
    /// Calls the persistent detection server's /detect endpoint via HTTP.
    /// No subprocess is started per call — the model stays warm in the server process.
    /// </summary>
    public static class MegaDetectorClient
    {
        private const string DetectUrl  = "http://127.0.0.1:5050/detect";
        private const int    MaxRetries = 3;

        // Shared client — created once, safe to reuse across threads.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        /// <summary>
        /// Uploads the image to the local detection server and returns MegaDetector results.
        /// </summary>
        public static async Task<MegaDetectorResponse> AnalyzeImageAsync(string imagePath)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found", imagePath);

            byte[] imageBytes = File.ReadAllBytes(imagePath);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using var content      = new MultipartFormDataContent();
                    using var imageContent = new ByteArrayContent(imageBytes);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Add(imageContent, "image", Path.GetFileName(imagePath));

                    var response = await _http.PostAsync(DetectUrl, content).ConfigureAwait(false);
                    var body     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<MegaDetectorResponse>(body, options);
                }
                catch (Exception ex) when (attempt < MaxRetries)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MegaDetector] Attempt {attempt} failed: {ex.Message}. Retrying in 1 s...");
                    await Task.Delay(1_000).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(
                "MegaDetector server failed to respond after all retries. Is detection_server.py running?");
        }
    }
}
