using System.CommandLine;
using System.Diagnostics;
using L5xploderLib;
using L5xGitLib;

namespace L5xCommands.Commands;

public static class PullRequest
{
    public static Command Command
    {
        get
        {
            var command = new Command("pullrequest", "Create a branch from unpushed commits, push it, and open a GitHub pull request.");

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

        var repoRoot = GetRepoRoot(config.DestinationPath);
        if (repoRoot == null)
        {
            Console.Error.WriteLine($"No Git repository found at {config.DestinationPath}.");
            return;
        }

        // Check for unpushed commits — try upstream first, fall back to all recent commits
        var hasUpstream = RunGit(repoRoot, "rev-parse --abbrev-ref @{u}")?.Trim();
        string? unpushedLog;
        if (!string.IsNullOrWhiteSpace(hasUpstream) && !hasUpstream.Contains("fatal"))
        {
            unpushedLog = RunGit(repoRoot, "log @{u}..HEAD --oneline");
        }
        else
        {
            unpushedLog = RunGit(repoRoot, "log --oneline -20");
        }

        if (string.IsNullOrWhiteSpace(unpushedLog))
        {
            Console.WriteLine("No commits found. Please commit first.");
            return;
        }

        Console.WriteLine("Unpushed commits:");
        Console.WriteLine(unpushedLog);
        Console.WriteLine();

        // Prompt for branch name
        Console.Write("Branch name: ");
        var branchName = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Console.WriteLine("Branch name is required. Aborting.");
            return;
        }

        // Prompt for PR title
        Console.Write("Pull request title: ");
        var prTitle = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(prTitle))
        {
            Console.WriteLine("PR title is required. Aborting.");
            return;
        }

        // Prompt for PR description
        Console.WriteLine("Pull request description (press Enter twice to finish):");
        var descriptionLines = new List<string>();
        string? line;
        do
        {
            Console.Write("> ");
            line = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                descriptionLines.Add(line);
            }
        } while (!string.IsNullOrWhiteSpace(line));

        var prDescription = string.Join(Environment.NewLine, descriptionLines);

        // Get current branch to use as base
        var currentBranch = RunGit(repoRoot, "rev-parse --abbrev-ref HEAD")?.Trim();

        // Create new branch
        Console.WriteLine($"Creating branch '{branchName}'...");
        var createResult = RunGit(repoRoot, $"checkout -b \"{branchName}\"");
        if (createResult == null)
        {
            Console.Error.WriteLine("Failed to create branch.");
            return;
        }

        // Push branch to remote
        Console.WriteLine($"Pushing branch '{branchName}' to origin...");
        var pushResult = RunGit(repoRoot, $"push -u origin \"{branchName}\"");
        if (pushResult == null)
        {
            Console.Error.WriteLine("Failed to push branch.");
            return;
        }
        Console.WriteLine("Branch pushed successfully.");

        // Create PR using GitHub CLI
        Console.WriteLine("Creating pull request...");
        var baseBranch = currentBranch ?? "main";
        var ghArgs = $"pr create --base \"{baseBranch}\" --head \"{branchName}\" --title \"{EscapeQuotes(prTitle)}\" --body \"{EscapeQuotes(prDescription)}\"";
        var prResult = RunCommand(repoRoot, "gh", ghArgs);
        if (prResult != null)
        {
            Console.WriteLine(prResult);
            Console.WriteLine("Pull request created successfully.");
        }
        else
        {
            Console.Error.WriteLine("Failed to create pull request. Make sure 'gh' (GitHub CLI) is installed and authenticated.");
        }

        // Switch back to original branch
        if (currentBranch != null)
        {
            RunGit(repoRoot, $"checkout \"{currentBranch}\"");
        }
    }

    private static string? GetRepoRoot(string path)
    {
        var result = RunGit(path, "rev-parse --show-toplevel");
        return result?.Trim();
    }

    private static string? RunGit(string workingDir, string arguments)
    {
        return RunCommand(workingDir, "git", arguments);
    }

    private static string? RunCommand(string workingDir, string command, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
            {
                Console.Error.WriteLine(error);
            }

            // git push writes to stderr even on success, so return output + error
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error running {command}: {ex.Message}");
            return null;
        }
    }

    private static string EscapeQuotes(string input)
    {
        return input.Replace("\"", "\\\"");
    }
}
