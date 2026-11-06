namespace DiskDrift.Core.Config;

/// <summary>
/// A configured Probe. <see cref="Path"/> may contain the <c>{user}</c> template, which is expanded over
/// every profile under <c>C:\Users</c>. For <see cref="ProbeKind.Vss"/> the path is the Volume, e.g. <c>C:</c>.
/// </summary>
public sealed class ProbeDefinition
{
    public const string UserTemplate = "{user}";

    public string Name { get; set; } = "";
    public ProbeKind Kind { get; set; } = ProbeKind.Dir;
    public string Path { get; set; } = "";

    public bool IsPerUser => Path.Contains(UserTemplate, StringComparison.OrdinalIgnoreCase);
}
