namespace NexusDownloader.Models
{
    public class GameFacet
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public int Count { get; set; }

        public override string ToString()
            => Id == null ? Name : $"{Name} ({Count})";
    }
}