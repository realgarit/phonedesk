using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class PortabilityFileService : IPortabilityFileService
{
    private readonly ITranslationService _translationService;

    public PortabilityFileService(ITranslationService translationService)
    {
        _translationService = translationService;
    }

    public async Task<PortableTextFile?> OpenJsonAsync(string title)
    {
        var window = GetMainWindow();
        if (window is null)
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { CreateJsonFileType() },
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync();
        return new PortableTextFile(file.Name, Describe(file), content);
    }

    public async Task<string?> SaveJsonAsync(string title, string suggestedFileName, string content)
    {
        var window = GetMainWindow();
        if (window is null)
        {
            return null;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { CreateJsonFileType() },
        });
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content);
        await writer.FlushAsync();
        return Describe(file);
    }

    private static Avalonia.Controls.Window? GetMainWindow()
        => Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private static string Describe(IStorageItem item)
        => item.Path.IsFile ? item.Path.LocalPath : item.Path.ToString();

    private FilePickerFileType CreateJsonFileType()
        => new(_translationService.Get(UiTextKey.VariablesJsonFiles))
        {
            Patterns = new[] { "*.json" },
            MimeTypes = new[] { "application/json" },
        };
}
