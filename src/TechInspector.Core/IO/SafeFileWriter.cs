using System;
using System.IO;

namespace TechInspector.Core.IO
{
    /// <summary>
    /// Writes a file in one step: the new bytes go to a temporary file beside the target, then
    /// replace it. A crash or power cut leaves either the old file or the new one, never half of each.
    /// </summary>
    public static class SafeFileWriter
    {
        public const string TempSuffix = ".ti-new";

        public static void WriteAtomic(string path, byte[] bytes)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
                throw new UnauthorizedAccessException($"{Path.GetFileName(path)} is read-only, so it can't be written.");

            var temp = path + TempSuffix;
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch (IOException) { /* best effort */ }
                }
            }
        }
    }
}
