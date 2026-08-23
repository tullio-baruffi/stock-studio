namespace System.IO
{
    public static class FileStreamExtensions
    {
        public static string GetExtension(this FileStream fileStream)
        {
            var fileName = fileStream.Name;
            var extension = Path.GetExtension(fileName);
            return extension?.Replace(".", string.Empty);
        }
    }
}