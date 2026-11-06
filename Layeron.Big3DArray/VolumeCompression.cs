namespace Layeron.Big3DArray
{
    /// <summary>
    /// Encoding used when writing a volume file. Loading detects the encoding from the stream, so
    /// files written with either value remain loadable.
    /// </summary>
    public enum VolumeCompression
    {
        /// <summary>
        /// Store blocks as-is. Fastest to read and write; roughly 1.6x larger on disk for clinical
        /// data, because compression costs more CPU than the saved I/O on modern storage.
        /// </summary>
        None,

        /// <summary>
        /// GZip the whole stream. Only worth it for transfer or archival, where a smaller file
        /// matters more than load time.
        /// </summary>
        GZip,
    }
}
