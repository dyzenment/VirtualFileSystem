// Dytools.VirtualFileSystem sample runner.
//
// Pick a demo interactively, or pass one as an argument to skip the menu:
//   dotnet run -- basic
//   dotnet run -- s3         (configure via VFS_S3_* env vars or interactive prompts)
//   dotnet run -- azure      (configure via VFS_AZURE_* env vars or interactive prompts)
//   dotnet run -- sharepoint (configure via VFS_SP_* env vars or interactive prompts)

using System.Text;
using Dytools.VirtualFileSystem.Sample;

Console.OutputEncoding = Encoding.UTF8;

var choice = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : PromptMenu();

switch (choice)
{
    case "basic" or "1":
        await BasicDemo.RunAsync();
        break;
    case "s3" or "2":
        await CloudSmokeTest.RunAsync("s3");
        break;
    case "azure" or "3":
        await CloudSmokeTest.RunAsync("azure");
        break;
    case "sharepoint" or "4":
        await CloudSmokeTest.RunAsync("sharepoint");
        break;
    case "":
        Console.WriteLine("Nothing selected.");
        break;
    default:
        Console.WriteLine($"Unknown option '{choice}'. Use: basic, s3, azure, or sharepoint.");
        break;
}

static string PromptMenu()
{
    Console.WriteLine("Dytools.VirtualFileSystem - sample");
    Console.WriteLine();
    Console.WriteLine("  1) Basic demo            (in-memory: aliases, symlinks, deduplication)");
    Console.WriteLine("  2) S3 smoke test         (live bucket)");
    Console.WriteLine("  3) Azure smoke test      (live container)");
    Console.WriteLine("  4) SharePoint smoke test (live drive + delta)");
    Console.WriteLine();
    Console.Write("Select [1-4]: ");
    return Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
}
