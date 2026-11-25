namespace MPWS.Models;

public sealed record Finding(string Title, string Status, string Evidence, string Meaning, string? Guide = null, string? Repair = null)
{
    public bool CanRestore => Guide != null || Repair != null;
}
public sealed record Article(string App, string Title, string Tags, string[] Steps, string Source);
public sealed record Change(string Name, int Original, string Machine, DateTime Created);
