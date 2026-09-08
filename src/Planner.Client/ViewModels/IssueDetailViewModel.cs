using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Planner.Client.Services;
using Planner.Contracts.Auth;
using Planner.Contracts.Enums;
using Planner.Contracts.Issues;

namespace Planner.Client.ViewModels;

public sealed record RelatedIssueRow(IssueRelationDto Relation)
{
    public Guid IssueId => Relation.IssueId;
    public string Caption => $"{Relation.IssueKey}  {Relation.IssueTitle}";
    public string Relationship => Relation.Type switch
    {
        IssueRelationType.Blocks => Relation.IsOutgoing ? "Blocks" : "Blocked by",
        IssueRelationType.Duplicates => Relation.IsOutgoing ? "Duplicates" : "Duplicated by",
        _ => "Related"
    };
}

public sealed partial class IssueDetailViewModel(
    PlannerApiClient api, ILoggerFactory loggerFactory, MeResponse caller, Guid issueId)
    : ViewModelBase, IWorkspaceContent, IUnsavedWork
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public Guid IssueId => issueId;
    public string Title => Detail?.Key ?? "Issue";
    public string? Subtitle => Detail?.Title;
    public string StatusSummary => $"{Children.Count} sub-issues · {Comments.Count} comments · {Attachments.Count} attachments";
    public bool HasUnsavedChanges => Editor?.HasUnsavedChanges == true ||
        !string.IsNullOrWhiteSpace(CommentBody) || !string.IsNullOrWhiteSpace(AttachmentName) ||
        !string.IsNullOrWhiteSpace(AttachmentUrl);
    public string UnsavedSummary => "This issue has unsaved edits or a contribution you have not posted.";
    public bool IsWorking => IsBusy || IsLoading || Editor?.IsBusy == true;
    public bool CanEdit => Detail is { } issue && caller.Role != "guest" &&
        (caller.Role is "owner" or "admin" || caller.Teams.Any(t =>
            t.TeamId == issue.TeamId && t.Role is "Lead" or "Member"));
    public bool CanComment => Detail is { } issue &&
        (caller.Role is "owner" or "admin" || caller.Teams.Any(t => t.TeamId == issue.TeamId &&
            (caller.Role == "guest" || t.Role is "Member" or "Lead")));
    public bool HasParent => Detail?.ParentId is not null;
    public event Action<Guid>? NavigateRequested;
    public event Action? BackRequested;
    public event Action<IssueDetail>? CreateChildRequested;

    [ObservableProperty] public partial IssueEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial IssueDetail? Detail { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? CommentBody { get; set; }
    [ObservableProperty] public partial string? AttachmentName { get; set; }
    [ObservableProperty] public partial string? AttachmentUrl { get; set; }
    [ObservableProperty] public partial string? SearchText { get; set; }
    [ObservableProperty] public partial IssueSummary? SelectedRelatedIssue { get; set; }
    [ObservableProperty] public partial IssueRelationType SelectedRelationType { get; set; }
    public IReadOnlyList<IssueRelationType> RelationTypes { get; } = Enum.GetValues<IssueRelationType>();
    public ObservableCollection<IssueSummary> Children { get; } = [];
    public ObservableCollection<CommentDto> Comments { get; } = [];
    public ObservableCollection<AttachmentDto> Attachments { get; } = [];
    public ObservableCollection<RelatedIssueRow> Relations { get; } = [];
    public ObservableCollection<IssueSummary> SearchResults { get; } = [];

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        try
        {
            await RefreshContributionsAsync(ct);
            var detail = Detail!;
            var teamName = caller.Teams.FirstOrDefault(t => t.TeamId == detail.TeamId)?.TeamName ?? detail.Key;
            var editor = IssueEditorViewModel.ForEdit(api,
                loggerFactory.CreateLogger<IssueEditorViewModel>(), detail.TeamId, teamName, issueId);
            await editor.LoadAsync(ct);
            ct.ThrowIfCancellationRequested();
            editor.Cancelled += () => BackRequested?.Invoke();
            editor.Saved += saved =>
            {
                if (saved.ArchivedAt is not null)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => BackRequested?.Invoke());
                else _ = RefreshContributionsAsync(CancellationToken.None);
            };
            Editor = editor;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
        finally { IsLoading = false; }
    }

    // Refresh collaboration separately so someone else's comment never replaces an editing draft.
    public async Task RefreshContributionsAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var detail = await api.GetIssueAsync(issueId, ct);
            var comments = new List<CommentDto>();
            for (var page = 1; ; page++)
            {
                var result = await api.GetCommentsAsync(issueId, page, ct);
                comments.AddRange(result.Items);
                if (!result.HasNext) break;
            }
            ct.ThrowIfCancellationRequested();
            Detail = detail;
            Replace(Children, detail.Children);
            Replace(Relations, detail.Relations.Select(r => new RelatedIssueRow(r)));
            Replace(Attachments, detail.Attachments);
            Replace(Comments, comments);
            foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(StatusSummary),
                         nameof(CanEdit), nameof(CanComment), nameof(HasParent) })
                OnPropertyChanged(name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException)
        {
            Error = ex.Message;
            if (Detail is null) throw;
        }
        finally { _refreshLock.Release(); }
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || IsLoading) return;
        IsBusy = true;
        Error = null;
        try { await action(); }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException or TaskCanceledException)
        { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    public Task UploadAsync(string name, byte[] bytes) => RunAsync(async () =>
    {
        if (!CanComment) return;
        if (bytes.LongLength > 20 * 1024 * 1024)
        {
            Error = "Attachments must be 20 MB or smaller.";
            return;
        }
        await api.UploadAttachmentAsync(issueId, name, bytes, CancellationToken.None);
        await RefreshContributionsAsync(CancellationToken.None);
    });

    public Task<byte[]> DownloadAsync(Guid id) => api.DownloadAttachmentAsync(id, CancellationToken.None);

    [RelayCommand]
    private Task PostCommentAsync() => RunAsync(async () =>
    {
        if (!CanComment || string.IsNullOrWhiteSpace(CommentBody)) return;
        await api.CreateCommentAsync(issueId, CommentBody.Trim(), CancellationToken.None);
        CommentBody = null;
        await RefreshContributionsAsync(CancellationToken.None);
        OnPropertyChanged(nameof(StatusSummary));
    });

    [RelayCommand]
    private Task AddAttachmentAsync() => RunAsync(async () =>
    {
        if (!CanComment) return;
        if (string.IsNullOrWhiteSpace(AttachmentName) ||
            !Uri.TryCreate(AttachmentUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            Error = "Enter a file name and an http or https link to a shared file.";
            return;
        }
        await api.CreateAttachmentAsync(issueId,
            new CreateAttachmentRequest(AttachmentName.Trim(), uri.AbsoluteUri), CancellationToken.None);
        AttachmentName = AttachmentUrl = null;
        await RefreshContributionsAsync(CancellationToken.None);
        OnPropertyChanged(nameof(StatusSummary));
    });

    [RelayCommand]
    private Task SearchAsync() => RunAsync(async () =>
    {
        SelectedRelatedIssue = null;
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        var result = await api.SearchIssuesAsync(SearchText.Trim(), CancellationToken.None);
        Replace(SearchResults, result.Items.Where(i => i.Id != issueId));
        if (SearchResults.Count == 0) Error = "No matching issues.";
    });

    [RelayCommand]
    private Task AddRelationAsync() => RunAsync(async () =>
    {
        if (!CanEdit || SelectedRelatedIssue is not { } target) return;
        await api.CreateRelationAsync(issueId,
            new CreateIssueRelationRequest(target.Id, SelectedRelationType), CancellationToken.None);
        await RefreshContributionsAsync(CancellationToken.None);
        SelectedRelatedIssue = null;
        SearchResults.Clear();
        SearchText = null;
    });

    [RelayCommand]
    private Task RemoveRelationAsync(RelatedIssueRow? row) => RunAsync(async () =>
    {
        if (!CanEdit || row is null) return;
        await api.DeleteRelationAsync(issueId, row.Relation.Id, CancellationToken.None);
        Relations.Remove(row);
    });

    [RelayCommand] private void OpenIssue(Guid id) => NavigateRequested?.Invoke(id);
    [RelayCommand] private void OpenParent() { if (Detail?.ParentId is { } id) NavigateRequested?.Invoke(id); }
    [RelayCommand] private void Back() => BackRequested?.Invoke();
    [RelayCommand] private void CreateChild() { if (CanEdit && Detail is { } detail) CreateChildRequested?.Invoke(detail); }
}
