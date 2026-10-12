using LintelAI.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace LintelAI.Views;

public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = "Choose a project folder",
            AllowMultiple = false
        };

        if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder);

        var picked = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } }
            }
        });
        return file?.TryGetLocalPath();
    }
}
