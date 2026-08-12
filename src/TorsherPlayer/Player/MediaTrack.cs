namespace TorsherPlayer.Player;

public sealed record MediaTrack(int Id, string Type, string Title, string Lang, string Codec, bool Selected, bool Default, bool Forced, bool External)
{
    public string Label
    {
        get
        {
            var p = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) p.Add(Title);
            if (!string.IsNullOrWhiteSpace(Lang)) p.Add(Lang.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Codec)) p.Add(Codec.ToUpperInvariant());
            return p.Count == 0 ? $"Дорожка {Id}" : string.Join(" · ", p);
        }
    }
}
