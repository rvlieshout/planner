using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Planner.Client.ViewModels;
using Planner.Contracts.Issues;

namespace Planner.Client.Views;

public partial class IssueDetailView : UserControl
{
    public IssueDetailView() => InitializeComponent();

    private void OnChildActivated(object? sender, TappedEventArgs e)
    {
        if (IssueRows.From(e.Source) is { } card && DataContext is IssueDetailViewModel model)
            model.OpenIssueCommand.Execute(card.Id);
    }

    private void OnChildKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: IssueCardViewModel card } &&
            DataContext is IssueDetailViewModel model)
        {
            model.OpenIssueCommand.Execute(card.Id);
            e.Handled = true;
        }
    }

    private async void UploadFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IssueDetailViewModel model || model.IsWorking ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach a file", AllowMultiple = false
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var source = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > 20 * 1024 * 1024)
                {
                    model.Error = "Attachments must be 20 MB or smaller.";
                    return;
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }
            await model.UploadAsync(file.Name, buffer.ToArray());
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }

    private async void OpenAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: AttachmentDto attachment } ||
            DataContext is not IssueDetailViewModel model ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (attachment.StorageUri == $"planner-attachment:{attachment.Id}")
            {
                using var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save attachment", SuggestedFileName = attachment.FileName
                });
                if (target is null) return;
                var bytes = await model.DownloadAsync(attachment.Id);
                await using var output = await target.OpenWriteAsync();
                output.SetLength(0);
                await output.WriteAsync(bytes);
            }
            else if (Uri.TryCreate(attachment.StorageUri, UriKind.Absolute, out var uri) &&
                     uri.Scheme is "http" or "https")
            {
                if (!await top.Launcher.LaunchUriAsync(uri))
                    model.Error = "Could not open this attachment.";
            }
            else model.Error = "This attachment does not have a supported file link.";
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
}
