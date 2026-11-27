namespace PhoneDesk.Services.Interfaces;

public sealed record PortableTextFile(string Name, string Location, string Content);

public interface IPortabilityFileService
{
    Task<PortableTextFile?> OpenJsonAsync(string title);
    Task<string?> SaveJsonAsync(string title, string suggestedFileName, string content);

    /// <summary>Lets the operator choose a folder on this computer. Null when they cancel, or when the folder is not a local one.</summary>
    Task<string?> PickFolderAsync(string title);
}
