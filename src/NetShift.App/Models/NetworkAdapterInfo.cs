namespace NetShift.App.Models;

public sealed class NetworkAdapterInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public required int InterfaceIndex { get; init; }
    public bool IsVpn { get; init; }

    public override string ToString() => $"{Name}  ·  {Status}";
}
