using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpeciesDetector
{
    public class SpeciesNetResult
    {
        public string top_species { get; set; }
        public float  confidence  { get; set; }
    }

    public class BioClipResult
    {
        public bool   target_match  { get; set; }
        public float  target_score  { get; set; }
        public string top_species   { get; set; }
        public float  top_score     { get; set; }
    }

    public class ClassificationResponse
    {
        public string           status    { get; set; }
        public string           error     { get; set; }
        /// <summary>Base64-encoded JPEG of the cropped animal region.</summary>
        public string           crop_b64  { get; set; }
        public SpeciesNetResult speciesnet { get; set; }
        public BioClipResult    bioclip   { get; set; }
    }

    /// <summary>
    /// Calls the persistent detection server's /classify endpoint via HTTP.
    /// Crops, runs SpeciesNet + BioCLIP, and returns results + the crop as base64.
    /// Discord posting is handled separately in <see cref="DiscordClient"/>.
    /// </summary>
    public static class ClassifierClient
    {
        private const string ClassifyUrl = "http://127.0.0.1:5050/classify";
        private const int    MaxRetries  = 3;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120),
        };

        /// <summary>
        /// Uploads the image to the local server, crops to <paramref name="bbox"/>,
        /// and returns SpeciesNet + BioCLIP results along with a base64 crop.
        /// </summary>
        public static async Task<ClassificationResponse> ClassifyAnimalAsync(
            string    imagePath,
            float[]   bbox,
            AppConfig config)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found", imagePath);
            if (bbox == null || bbox.Length != 4)
                throw new ArgumentException("bbox must have exactly 4 elements [xmin, ymin, xmax, ymax]");

            var inv            = System.Globalization.CultureInfo.InvariantCulture;
            var candidatesJson = JsonSerializer.Serialize(config.CandidateSpecies);
            var imageBytes     = File.ReadAllBytes(imagePath);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using var content      = new MultipartFormDataContent();
                    using var imageContent = new ByteArrayContent(imageBytes);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Add(imageContent,                                    "image",                Path.GetFileName(imagePath));
                    content.Add(new StringContent(bbox[0].ToString(inv)),        "xmin");
                    content.Add(new StringContent(bbox[1].ToString(inv)),        "ymin");
                    content.Add(new StringContent(bbox[2].ToString(inv)),        "xmax");
                    content.Add(new StringContent(bbox[3].ToString(inv)),        "ymax");
                    content.Add(new StringContent(config.TargetSpecies),         "target_species");
                    content.Add(new StringContent(candidatesJson),               "candidate_species");
                    content.Add(new StringContent(config.CountryCode ?? ""),     "country_code");
                    content.Add(new StringContent(
                        config.ConfidenceThreshold.ToString(inv)),               "confidence_threshold");

                    var response = await _http.PostAsync(ClassifyUrl, content).ConfigureAwait(false);
                    var body     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<ClassificationResponse>(body, options);
                }
                catch (Exception ex) when (attempt < MaxRetries)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Classifier] Attempt {attempt} failed: {ex.Message}. Retrying in 1 s...");
                    await Task.Delay(1_000).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(
                "Classifier server failed to respond after all retries. Is detection_server.py running?");
        }
    }
}
