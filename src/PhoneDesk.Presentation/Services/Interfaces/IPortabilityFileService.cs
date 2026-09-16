namespace PhoneDesk.Services.Interfaces;

public sealed record PortableTextFile(string Name, string Location, string Content);

public interface IPortabilityFileService
{
    Task<PortableTextFile?> OpenJsonAsync(string title);
    Task<string?> SaveJsonAsync(string title, string suggestedFileName, string content);
}
