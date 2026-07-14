using System.Windows.Media.Imaging;

namespace SpeciesDetector
{
    /// <summary>One entry in the in-app detection history list.</summary>
    public class DetectionRecord
    {
        public BitmapImage Thumbnail { get; set; }
        public string      Summary   { get; set; }
        public bool        IsMatch   { get; set; }
    }
}
