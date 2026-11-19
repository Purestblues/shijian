namespace ShiJian.Client.Shared.Models.Config;

public class ConfigModel
{
    public int Version { get; set; }

    public GeneralModel General { get; set; } = new();

    public BehaviorModel Behavior { get; set; } = new();

    public ShortcutModel Shortcut { get; set; } = new();

    public WebDavModel WebDav { get; set; } = new();

    public McpModel Mcp { get; set; } = new();
}
