using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Planner.Api.Endpoints;
using Planner.Domain.Entities;

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
