using System.Drawing.Imaging;

namespace System.Drawing
{
    public static class ImageExtensions
    {
        public static string ExtensionFromImageType(this Image image)
        {
            if (image.RawFormat.Equals(ImageFormat.Bmp))
            {
                return "Bmp";
            }
            else if (image.RawFormat.Equals(ImageFormat.MemoryBmp))
            {
                return "BMP";
            }
            else if (image.RawFormat.Equals(ImageFormat.Emf))
            {
                // Previously this branch tested Wmf and returned "Emf", which made the Wmf case
                // below unreachable and gave EMF pictures the wrong extension.
                return "Emf";
            }
            else if (image.RawFormat.Equals(ImageFormat.Wmf))
            {
                return "Wmf";
            }
            else if (image.RawFormat.Equals(ImageFormat.Gif))
            {
                return "Gif";
            }
            else if (image.RawFormat.Equals(ImageFormat.Jpeg))
            {
                return "Jpeg";
            }
            else if (image.RawFormat.Equals(ImageFormat.Png))
            {
                return "Png";
            }
            else if (image.RawFormat.Equals(ImageFormat.Tiff))
            {
                return "Tiff";
            }
            else if (image.RawFormat.Equals(ImageFormat.Exif))
            {
                return "Exif";
            }
            else if (image.RawFormat.Equals(ImageFormat.Icon))
            {
                return "Ico";
            }

            return "";
        }
    }
}