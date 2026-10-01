using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Planner.Api.Common;
using Planner.Api.Endpoints;
using Planner.Api.Realtime;
using Planner.Contracts.Enums;
using Planner.Domain.Entities;
using Planner.Domain.Identity;

var root = Path.Combine(Path.GetTempPath(), "planner-attachment-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var environment = new TestEnvironment { ContentRootPath = root };
    var defaults = new ConfigurationBuilder().Build();
    var customRoot = Path.Combine(root, "custom");
    var custom = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Attachments:Path"] = customRoot
    }).Build();

    foreach (var (config, directory) in new[]
    {
        (defaults, Path.Combine(root, "App_Data", "attachments")),
        (custom, customRoot)
    })
    {
        Directory.CreateDirectory(directory);
        var attachment = new Attachment();
        attachment.StorageUri = $"planner-attachment:{attachment.Id}";
        var path = Path.Combine(directory, attachment.Id.ToString("N"));
        File.WriteAllText(path, "uploaded bytes");
        IssueFileEndpoints.DeleteStoredFile(attachment, config, environment);
        Check(!File.Exists(path), "Uploaded file deleted from configured storage");
        IssueFileEndpoints.DeleteStoredFile(attachment, config, environment);

        File.WriteAllText(path, "external bytes");
        attachment.StorageUri = path;
        IssueFileEndpoints.DeleteStoredFile(attachment, config, environment);
        Check(File.Exists(path), "External file reference is preserved");
        attachment.StorageUri = $"planner-attachment:{Guid.NewGuid()}";
        IssueFileEndpoints.DeleteStoredFile(attachment, config, environment);
        Check(File.Exists(path), "Mismatched storage identifier is preserved");

        attachment.StorageUri = $"planner-attachment:{attachment.Id}";
        File.Delete(path);
        Directory.CreateDirectory(path);
        var failed = false;
        try { IssueFileEndpoints.DeleteStoredFile(attachment, config, environment); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "Storage failures propagate so the endpoint can retain the record");
    }

    var missing = new Attachment();
    missing.StorageUri = $"planner-attachment:{missing.Id}";
    environment.ContentRootPath = Path.Combine(root, "missing");
    IssueFileEndpoints.DeleteStoredFile(missing, defaults, environment);
    Console.WriteLine("PASS: Missing storage directory permits metadata removal");
}
finally
{
    Directory.Delete(root, recursive: true);
}

foreach (var link in new[] { "https://example.com/trace.har", "http://files.internal:8080/a%20b.pdf", "HTTPS://EXAMPLE.COM" })
{
    Check(IssueEndpoints.IsWebLink(link), $"Attachment link accepted: {link}");
}

foreach (var link in new[]
{
    "javascript:alert(1)", "data:text/html,<script>alert(1)</script>", "file:///etc/passwd", @"\\host\share\file",
    "/etc/passwd", @"C:\Windows\System32\calc.exe", "ms-msdt:/id", "//example.com/x", "example.com/x", "https://",
    $"planner-attachment:{Guid.NewGuid()}", "", " "
})
{
    Check(!IssueEndpoints.IsWebLink(link), $"Attachment link refused: '{link}'");
}

foreach (var url in new[] { "https://intranet.example.com/people/a.png", "http://files.internal/a.png", "/avatars/a.png" })
{
    Check(UserEndpoints.IsAvatarUrl(url), $"Avatar address accepted: {url}");
}

foreach (var url in new[]
{
    "javascript:alert(1)", "data:image/png;base64,AAAA", "//tracker.example/a.png", @"\\host\share\a.png", @"/a\b.png",
    "a.png", "", "https://example.com/" + new string('a', 2000)
})
{
    Check(!UserEndpoints.IsAvatarUrl(url), $"Avatar address refused: '{(url.Length > 60 ? url[..60] + "…" : url)}'");
}

{
    static Issue NewIssue(Team team, int number, string title) => new()
    {
        Team = team, TeamId = team.Id, Number = number, Title = title, Rank = "a0",
        State = new WorkflowState { TeamId = team.Id, Name = "Todo", Type = WorkflowStateType.Unstarted, Rank = "a0" },
        Creator = new AppUser { Email = "creator@planner.test", DisplayName = "Creator" }
    };

    var ours = new Team { Key = "OURS", Name = "Ours" };
    var theirs = new Team { Key = "THEIRS", Name = "Theirs" };
    var open = NewIssue(ours, 1, "The issue being read");
    var sibling = NewIssue(ours, 2, "Sibling");
    var hidden = NewIssue(theirs, 7, "Title from another team");

    open.OutgoingRelations.Add(new IssueRelation { SourceIssue = open, TargetIssue = sibling, Type = IssueRelationType.Related });
    open.OutgoingRelations.Add(new IssueRelation { SourceIssue = open, TargetIssue = hidden, Type = IssueRelationType.Blocks });
    open.IncomingRelations.Add(new IssueRelation { SourceIssue = hidden, TargetIssue = open, Type = IssueRelationType.Duplicates });

    var inOneTeam = Mapping.ToIssueDetail(open, [], 0, [ours.Id]).Relations;
    Check(inOneTeam.Count == 1 && inOneTeam[0].IssueTitle == "Sibling",
        "Relations to an issue in a team the reader cannot see are left out, in both directions");
    Check(Mapping.ToIssueDetail(open, [], 0, [ours.Id, theirs.Id]).Relations.Count == 3,
        "A reader of both teams sees every relation");

    var connections = new RealtimeConnections();
    var both = connections.Register("both", Guid.NewGuid());
    both.Teams = [ours.Id, theirs.Id];
    both.Issues[open.Id] = ours.Id;
    var one = connections.Register("one", Guid.NewGuid());
    one.Teams = [ours.Id];
    one.Issues[open.Id] = ours.Id;
    var elsewhere = connections.Register("elsewhere", Guid.NewGuid());
    elsewhere.Teams = [ours.Id, theirs.Id];

    Check(connections.Watching(open.Id, theirs.Id).SequenceEqual(["both"]),
        "A relation naming another team's issue is pushed only to watchers who can read that team");
    Check(connections.Watching(open.Id, ours.Id).Order().SequenceEqual(["both", "one"]),
        "A relation within the team is pushed to everyone with the issue open");
}

await Planner.Api.Checks.Base58Checks.RunAsync(Check);
Planner.Api.Checks.RankChecks.Run(Check);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Planner.Api.Checks";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
