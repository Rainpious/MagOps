using System.Text.Json.Serialization;

namespace MagOps.Infrastructure.Services;

public sealed class FolderLayoutDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "folder-layout-1";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("source_basis")]
    public string SourceBasis { get; set; } = "";

    [JsonPropertyName("principles")]
    public List<string> Principles { get; set; } = [];

    [JsonPropertyName("project_folders")]
    public List<FolderLayoutEntry> ProjectFolders { get; set; } = [];

    [JsonPropertyName("workarea_folders")]
    public List<FolderLayoutEntry> WorkareaFolders { get; set; } = [];

    [JsonPropertyName("operations")]
    public List<FolderOperationEntry> Operations { get; set; } = [];
}

public sealed class FolderLayoutEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "";
}

public sealed class FolderOperationEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("folder_key")]
    public string FolderKey { get; set; } = "";

    [JsonPropertyName("subfolder")]
    public string Subfolder { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("create_policy")]
    public string CreatePolicy { get; set; } = "on_write";
}
