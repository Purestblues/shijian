namespace ShiJian.Client.Shared.Models.Config;

/// <summary>本地 MCP AI 接入配置（默认关闭）</summary>
public class McpModel
{
    public bool Enabled { get; set; }

    public string Token { get; set; } = string.Empty;
}
