using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SpeciesDetector
{
    /// <summary>
    /// Posts a rich Discord webhook embed when a target species is detected.
    /// Moved from the Python sidecar to C# so it runs in the same process
    /// and doesn't require the Python requests library.
    /// </summary>
    public static class DiscordClient
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        /// <summary>
        /// Posts a detection alert to the configured Discord webhook.
        /// </summary>
        /// <param name="webhookUrl">Full Discord webhook URL.</param>
        /// <param name="cameraName">Human-readable camera name from XProtect config.</param>
        /// <param name="timestamp">UTC timestamp of the motion event.</param>
        /// <param name="targetSpecies">The species being searched for.</param>
        /// <param name="snetGuess">SpeciesNet top prediction label.</param>
        /// <param name="snetConfidence">SpeciesNet confidence [0–1].</param>
        /// <param name="bioclipScore">BioCLIP target-match score [0–1].</param>
        /// <param name="cropImageBytes">JPEG bytes of the cropped animal region.</param>
        /// <param name="cropFileName">Filename for the attachment (e.g. "crop_cam1_20260713.jpg").</param>
        /// <returns>True if Discord returned 200/204.</returns>
        public static async Task<bool> PostAlertAsync(
            string   webhookUrl,
            string   cameraName,
            DateTime timestamp,
            string   targetSpecies,
            string   snetGuess,
            float    snetConfidence,
            float    bioclipScore,
            byte[]   cropImageBytes,
            string   cropFileName)
        {
            if (string.IsNullOrEmpty(webhookUrl) || cropImageBytes == null || cropImageBytes.Length == 0)
                return false;

            try
            {
                // ── Choose embed colour by BioCLIP confidence ────────────────────
                // ≥ 80%  →  green   (0x57F287)
                // ≥ 60%  →  amber   (0xFEE75C)
                //  else  →  teal    (0x1ABC9C)
                int embedColor = bioclipScore >= 0.80f ? 0x57F287
                               : bioclipScore >= 0.60f ? 0xFEE75C
                               : 0x1ABC9C;

                string confidenceBar = BuildConfidenceBar(bioclipScore);
                string snetDisplay   = string.IsNullOrWhiteSpace(snetGuess)
                    ? "_unknown_"
                    : $"`{snetGuess}`";

                // ── Build the embed object ────────────────────────────────────────
                var embed = new
                {
                    title = "🎯  TARGET SPECIES DETECTED",
                    description =
                        $"The camera-trap pipeline has flagged a **{bioclipScore:P0}** confidence match " +
                        $"for ***{targetSpecies}***.\n" +
                         "⚠️  **Human verification required before any claim is made.**",
                    color = embedColor,
                    fields = new object[]
                    {
                        new { name = "📷  Camera",           value = $"`{cameraName}`",                      inline = true  },
                        new { name = "🕐  Timestamp (UTC)",  value = $"`{timestamp:yyyy-MM-dd  HH:mm:ss}`",  inline = true  },
                        new { name = "\u200b",               value = "\u200b",                               inline = false }, // row break

                        new { name = "🎯  BioCLIP Score",
                              value = $"**{bioclipScore:P1}**\n{confidenceBar}",
                              inline = true },
                        new { name = "🔬  SpeciesNet Guess",
                              value = $"{snetDisplay}\n({snetConfidence:P1} confidence)",
                              inline = true },
                        new { name = "\u200b",               value = "\u200b",                               inline = false }, // row break

                        new { name = "🦎  Target Species",
                              value = $"**{targetSpecies}**",
                              inline = false },
                    },
                    image  = new { url = $"attachment://{cropFileName}" },
                    footer = new { text = "Not Actually Extinct  |  MIP SDK Camera-Trap System  |  Verify before publishing" },
                    timestamp = timestamp.ToString("o"),   // ISO 8601 — Discord renders this as a relative timestamp
                };

                var payload     = new { embeds = new[] { embed } };
                var payloadJson = JsonSerializer.Serialize(payload);

                // ── Multipart POST (payload_json + file attachment) ───────────────
                using var form = new MultipartFormDataContent();

                var payloadContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                form.Add(payloadContent, "payload_json");

                var imgContent = new ByteArrayContent(cropImageBytes);
                imgContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(imgContent, "files[0]", cropFileName);

                var response = await _http.PostAsync(webhookUrl, form).ConfigureAwait(false);
                bool success  = response.IsSuccessStatusCode;

                if (!success)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[Discord] HTTP {(int)response.StatusCode}: {body}");
                }

                return success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Discord] PostAlertAsync failed: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a simple text progress-bar for the confidence score, e.g. "[████████░░] 80%".
        /// </summary>
        private static string BuildConfidenceBar(float score)
        {
            const int total  = 10;
            int filled = (int)Math.Round(score * total);
            return "[" + new string('█', filled) + new string('░', total - filled) + "]";
        }
    }
}
