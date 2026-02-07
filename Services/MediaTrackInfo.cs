namespace NewAxis.Services
{
    public enum MediaTrackType
    {
        Audio,
        Subtitle
    }

    public sealed class MediaTrackInfo
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Language { get; set; } = "";
        public bool IsSelected { get; set; }
    }
}
