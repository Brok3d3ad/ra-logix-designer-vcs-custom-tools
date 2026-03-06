
using System.CommandLine;
using System.Diagnostics;
using L5xploderLib;
using L5xGitLib;
using L5xGitLib.Services;

namespace L5xCommands.Commands;

public static class Difftool
{
    public static Command Command
    {
        get
        {
            var command = new Command("difftool", "A command to show the diff of HEAD with the previous commit.");

            var acdOption = new Option<string>("--acd", "-a")
            {
                Description = "The path to the ACD file",
                Required = true,
                Validators =
                {
                    optionValue => OptionValidator.FileExtension(optionValue, ".acd"),
                    OptionValidator.FileExists,
                }
            };

            command.Options.Add(acdOption);

            command.SetAction(parseResult =>
            {
                var acdPath = parseResult.GetValue(acdOption) ?? throw new ArgumentNullException(nameof(acdOption));

                Execute(acdPath);
            });

            return command;
        }
    }

    private static void Execute(string acdPath)
    {
        var configFilePath = Paths.GetL5xConfigFilePathFromAcdPath(acdPath);
        var config = L5xGitConfig.LoadFromFile(configFilePath);
        if (config == null)
        {
            Console.Error.WriteLine($"Configuration file not found at {configFilePath}. Please run 'l5xgit commit' first.");
            return;
        }

        using var gitService = GitService.Create(config.DestinationPath);
        if (gitService == null)
        {
            Console.Error.WriteLine($"Failed to initialize Git service for path: {config.DestinationPath}");
            return;
        }
        var repoRoot = gitService.RepoRoot;

        // Get list of changed files between HEAD~1 and HEAD
        var changedFiles = GetChangedFiles(repoRoot);
        if (changedFiles.Count == 0)
        {
            Console.WriteLine("No changes found between the last two commits.");
            return;
        }

        Console.WriteLine($"Found {changedFiles.Count} changed file(s). Opening diffs in VS Code...");

        // Open each changed file in VS Code's native side-by-side diff view
        foreach (var file in changedFiles)
        {
            var oldFile = GetFileAtRevision(repoRoot, file, "HEAD~1");
            var newFile = Path.Combine(repoRoot, file);

            if (oldFile is not null && File.Exists(newFile))
            {
                // Modified file — open side-by-side diff
                Process.Start(new ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = $"--diff \"{oldFile}\" \"{newFile}\"",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = true
                });
            }
            else if (File.Exists(newFile))
            {
                // New file — just open it
                Process.Start(new ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = $"\"{newFile}\"",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = true
                });
            }
        }
    }

    private static List<string> GetChangedFiles(string repoRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "diff --name-only HEAD~1 HEAD",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return new List<string>();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string? GetFileAtRevision(string repoRoot, string relativeFilePath, string revision)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"show {revision}:{relativeFilePath.Replace('\\', '/')}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var content = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0) return null;

            // Write old version to a temp file so VS Code can diff it
            var tempPath = Path.Combine(Path.GetTempPath(), $"l5xgit_old_{Path.GetFileName(relativeFilePath)}");
            File.WriteAllText(tempPath, content);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }
}