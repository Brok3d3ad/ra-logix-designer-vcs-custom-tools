using System.CommandLine;
using System.Diagnostics;
using L5xGitLib;
using L5xploderLib;
using L5xploderLib.Services;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

namespace L5xCommands.Commands;

public static class Pull
{
    public static Command Command
    {
        get
        {
            var command = new Command("pull", "Pull from remote, recompile changed ACD files, and reopen them in Logix Designer.");

            var acdOption = new Option<string>("--acd", "-a")
            {
                Description = "The path to the ACD file",
                Required = true,
                Validators =
                {
                    optionValue => OptionValidator.FileExtension(optionValue, ".acd"),
                }
            };

            command.Options.Add(acdOption);

            command.SetAction(parseResult =>
            {
                var acdPath = parseResult.GetValue(acdOption) ?? throw new ArgumentNullException(nameof(acdOption));
                return Execute(acdPath);
            });

            return command;
        }
    }

    private static async Task Execute(string acdPath)
    {
        var logger = new StdOutEventLogger();

        acdPath = Path.GetFullPath(acdPath);
        var repoRoot = FindRepoRoot(acdPath);
        if (repoRoot == null)
        {
            Console.Error.WriteLine("No Git repository found.");
            return;
        }

        // Discover all L5xGit config files in the repo
        var configs = DiscoverConfigs(repoRoot);
        if (configs.Count == 0)
        {
            Console.Error.WriteLine("No L5xGit configuration files found.");
            return;
        }

        Console.WriteLine($"Found {configs.Count} project(s):");
        foreach (var (cfgAcdPath, _) in configs)
        {
            var name = Path.GetFileNameWithoutExtension(cfgAcdPath);
            var openStatus = FindLogixDesignerProcess(name) != null ? " [OPEN]" : "";
            Console.WriteLine($"  - {Path.GetFileName(cfgAcdPath)}{openStatus}");
        }

        // Warn user
        Console.WriteLine();
        Console.WriteLine("WARNING: This will pull changes from remote and overwrite local ACD files.");
        Console.WriteLine("Any unsaved changes in Logix Designer will be LOST.");
        Console.Write("Continue? (y/n): ");
        var response = Console.ReadLine()?.Trim();
        if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Pull canceled.");
            return;
        }

        // Record which projects are currently open
        var openProjects = new Dictionary<string, Process>();
        foreach (var (cfgAcdPath, _) in configs)
        {
            var baseName = Path.GetFileNameWithoutExtension(cfgAcdPath);
            var proc = FindLogixDesignerProcess(baseName);
            if (proc != null)
            {
                openProjects[cfgAcdPath] = proc;
            }
        }

        // Close all open Logix Designer instances for these projects
        foreach (var (cfgAcdPath, proc) in openProjects)
        {
            logger?.Status(cfgAcdPath, $"Closing Logix Designer (PID {proc.Id})...");
            proc.CloseMainWindow();
            if (!proc.WaitForExit(30_000))
            {
                logger?.Status(cfgAcdPath, "Logix Designer did not close gracefully, forcing...");
                proc.Kill();
                proc.WaitForExit(10_000);
            }
            logger?.Status(cfgAcdPath, "Logix Designer closed.");
        }

        // Get HEAD before pull
        var headBefore = RunGit(repoRoot, "rev-parse HEAD")?.Trim();

        // Git pull
        Console.WriteLine();
        logger?.Status(repoRoot, "Pulling from remote...");
        var pullResult = RunGit(repoRoot, "pull");
        Console.WriteLine(pullResult);

        // Get HEAD after pull
        var headAfter = RunGit(repoRoot, "rev-parse HEAD")?.Trim();

        if (headBefore == headAfter)
        {
            Console.WriteLine("Already up to date. No recompilation needed.");
            // Reopen projects that were open
            ReopenProjects(openProjects.Keys, logger);
            return;
        }

        // Find which files changed
        var changedFiles = RunGit(repoRoot, $"diff --name-only {headBefore} {headAfter}")?.Trim() ?? "";

        // Recompile each project whose exploded content changed
        foreach (var (cfgAcdPath, config) in configs)
        {
            var relativeDestPath = Path.GetRelativePath(repoRoot, config.DestinationPath).Replace('\\', '/');
            var hasChanges = changedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(f => f.StartsWith(relativeDestPath, StringComparison.OrdinalIgnoreCase));

            if (!hasChanges)
            {
                Console.WriteLine($"No changes for {Path.GetFileName(cfgAcdPath)}, skipping recompile.");
                continue;
            }

            logger?.Status(cfgAcdPath, $"Recompiling {Path.GetFileName(cfgAcdPath)}...");
            await RecompileAcd(cfgAcdPath, config, logger);
        }

        // Reopen projects that were open before
        ReopenProjects(openProjects.Keys, logger);

        Console.WriteLine();
        Console.WriteLine("Pull complete.");
    }

    private static async Task RecompileAcd(string acdPath, L5xGitConfig config, StdOutEventLogger? logger)
    {
        var acdFileName = Path.GetFileName(acdPath);
        using var tempAcdFile = TempFile.FromSuggestedFileName(acdFileName);
        using var tempL5xFile = TempFile.FromTempFileWithNewExtension(tempAcdFile, ".L5X");

        logger?.Status(acdPath, $"Imploding from {config.DestinationPath}...");
        L5xImploder.Implode(
            outputFilePath: tempL5xFile.Path,
            configs: L5xDefaultConfig.DefaultConfig,
            persistenceService: PersistenceServiceFactory.Create(
                explodedDir: config.DestinationPath,
                options: L5xSerializationOptions.LoadFromFile(Paths.GetOptionsFilePath(config.DestinationPath))
                    ?? L5xSerializationOptions.DefaultOptions));

        logger?.Status(acdPath, "Converting L5X to ACD...");
        using LogixProject project = await LogixProject.OpenLogixProjectAsync(tempL5xFile.Path, new StdOutEventLogger());
        await project.SaveAsAsync(tempAcdFile.Path, true);

        var fileBytes = new FileInfo(tempAcdFile.Path).Length;
        if (fileBytes == 0)
        {
            Console.Error.WriteLine($"Failed to recompile {acdFileName}.");
            return;
        }

        // Backup existing ACD
        if (File.Exists(acdPath))
        {
            var backupPath = acdPath + ".bak";
            File.Copy(acdPath, backupPath, true);
            logger?.Status(acdPath, $"Backup saved to {backupPath}");
        }

        File.Move(tempAcdFile.Path, acdPath, true);
        logger?.Status(acdPath, "ACD restored successfully.");
    }

    private static void ReopenProjects(IEnumerable<string> acdPaths, StdOutEventLogger? logger)
    {
        foreach (var acdPath in acdPaths)
        {
            if (File.Exists(acdPath))
            {
                logger?.Status(acdPath, "Reopening in Logix Designer...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = acdPath,
                    UseShellExecute = true,
                });
            }
        }
    }

    private static List<(string AcdPath, L5xGitConfig Config)> DiscoverConfigs(string repoRoot)
    {
        var results = new List<(string, L5xGitConfig)>();

        foreach (var ymlFile in Directory.GetFiles(repoRoot, "*_L5xGit.yml", SearchOption.AllDirectories))
        {
            var config = L5xGitConfig.LoadFromFile(ymlFile);
            if (config == null) continue;

            // Derive ACD path from config filename: CDW5_MCM09_L5xGit.yml → CDW5_MCM09.ACD
            var dir = Path.GetDirectoryName(ymlFile) ?? "";
            var ymlName = Path.GetFileNameWithoutExtension(ymlFile); // CDW5_MCM09_L5xGit
            var acdBaseName = ymlName.Replace("_L5xGit", "");
            var acdPath = Path.Combine(dir, acdBaseName + ".ACD");

            results.Add((acdPath, config));
        }

        return results;
    }

    private static string? FindRepoRoot(string path)
    {
        var configPath = Paths.GetL5xConfigFilePathFromAcdPath(path);
        var config = L5xGitConfig.LoadFromFile(configPath);
        var searchPath = config?.DestinationPath ?? Path.GetDirectoryName(path) ?? path;
        return RunGit(searchPath, "rev-parse --show-toplevel")?.Trim();
    }

    private static Process? FindLogixDesignerProcess(string acdBaseName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("LogixDesigner"))
            {
                try
                {
                    if (proc.MainWindowTitle.Contains(acdBaseName, StringComparison.OrdinalIgnoreCase))
                        return proc;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? RunGit(string workingDir, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
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

            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch
        {
            return null;
        }
    }
}
