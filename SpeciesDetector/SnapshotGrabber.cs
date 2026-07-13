using System;
using System.IO;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Live;

namespace SpeciesDetector
{
    /// <summary>
    /// Grabs a single JPEG snapshot from a camera via JPEGLiveSource.
    /// Pattern adapted from the MediaLiveViewer and CameraStreamResolution samples
    /// (mipsdk-samples-component repo).
    ///
    /// JPEGLiveSource works by streaming live frames as events; for a one-shot
    /// snapshot we subscribe, wait for the first frame to arrive on a reset event,
    /// then immediately unsubscribe and close to avoid leaking a live stream.
    /// </summary>
    static class SnapshotGrabber
    {
        private const int TimeoutMs = 10_000; // 10 s — give the recording server time to respond

        /// <summary>
        /// Grabs one JPEG frame from <paramref name="cameraItem"/> and saves it
        /// to <paramref name="outputPath"/>.
        /// </summary>
        /// <returns>True if the file was saved; false on timeout or error.</returns>
        public static bool GrabAndSave(Item cameraItem, string outputPath)
        {
            if (cameraItem == null)
                throw new ArgumentNullException(nameof(cameraItem));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            
            // --- TEST OVERRIDE ---
            // Use the test image instead of pulling from the actual camera
            string testImagePath = @"c:\Users\Sami\Documents\milestones\extinct an finder\milestone-extinct-animal-finder\SpeciesDetector\test_image1.png";
            if (File.Exists(testImagePath))
            {
                File.Copy(testImagePath, outputPath, true);
                return true;
            }
            // ---------------------

            var gotFrame   = new ManualResetEventSlim(false);
            var jpegSource = new JPEGLiveSource(cameraItem);
            bool saved     = false;

            // Full native resolution: Width=0 / Height=0 means "don't rescale"
            jpegSource.Width          = 0;
            jpegSource.Height         = 0;
            jpegSource.LiveModeStart  = true;

            EventHandler handler = null;
            handler = (sender, e) =>
            {
                var args = e as LiveContentEventArgs;
                if (args?.LiveContent != null)
                {
                    try
                    {
                        File.WriteAllBytes(outputPath, args.LiveContent.Content);
                        saved = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"SnapshotGrabber write error: {ex.Message}");
                    }
                    finally
                    {
                        args.LiveContent.Dispose();
                        gotFrame.Set();
                    }
                }
                else if (args?.Exception != null)
                {
                    System.Diagnostics.Debug.WriteLine($"SnapshotGrabber LiveContent error: {args.Exception.Message}");
                    gotFrame.Set();
                }
            };

            try
            {
                jpegSource.Init();
                jpegSource.LiveContentEvent += handler;
                gotFrame.Wait(TimeoutMs);
            }
            finally
            {
                jpegSource.LiveContentEvent -= handler;
                jpegSource.Close();
                gotFrame.Dispose();
            }

            return saved;
        }
    }
}
