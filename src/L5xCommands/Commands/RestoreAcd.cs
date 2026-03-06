using L5xGitLib;
using L5xploderLib;
using L5xploderLib.Services;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using System.CommandLine;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace L5xCommands.Commands;

public static class RestoreAcd
{
    public static Command Command
    {
        get
        {
            var command = new Command("restoreacd", "The inverse of the commit command, this command will overwrite the chosen ACD file with one imploded from Git.");

            var acdOption = new Option<string?>("--acd", "-a")
            {
                Description = "Path to the ACD file to overwrite. If not provided will prompt for path.",
                Validators = 
                {
                    optionValue => OptionValidator.FileExtension(optionValue, ".acd"),
                }
            };

            command.Options.Add(acdOption);

            command.SetAction(parseResult =>
            {
                var acdPath = parseResult.GetValue(acdOption);

                return Execute(acdPath);
            });

            return command;
        }
    }

    private static async Task Execute(string? acdPath)
    {
        var logger = new StdOutEventLogger();

        if (string.IsNullOrWhiteSpace(acdPath))
        {
            acdPath = UserPrompts.PromptForAcdFilePath();
        }

        acdPath = Path.GetFullPath(acdPath);

        var config = UserPrompts.InitializeConfigPromptIfNeeded(acdPath, logger);

        if (!UserPrompts.PromptForFileOverwriteIfExists(acdPath))
        {
            return;
        }

        var acdFileName = Path.GetFileName(acdPath);
        using var tempAcdFile = TempFile.FromSuggestedFileName(acdFileName);
        using var tempL5xFile = TempFile.FromTempFileWithNewExtension(tempAcdFile, ".L5X");

        logger?.Status(tempL5xFile.Path, $"Restoring L5x from {config.DestinationPath}...");
        L5xImploder.Implode(
            outputFilePath: tempL5xFile.Path,
            configs: L5xDefaultConfig.DefaultConfig,
            persistenceService: PersistenceServiceFactory.Create(
                explodedDir: config.DestinationPath,
                options: L5xSerializationOptions.LoadFromFile(Paths.GetOptionsFilePath(config.DestinationPath)) ?? L5xSerializationOptions.DefaultOptions));
        logger?.Status(tempL5xFile.Path, "Restoration of L5x complete.");

        await ConvertL5xToAcd(tempL5xFile.Path, tempAcdFile.Path);

        // Close Logix Designer if it has this ACD open, then move the file
        var acdBaseName = Path.GetFileNameWithoutExtension(acdPath);
        var logixProcess = FindLogixDesignerProcess(acdBaseName);
        bool wasOpen = logixProcess is not null;

        if (logixProcess is not null)
        {
            logger?.Status(acdPath, $"Closing Logix Designer (PID {logixProcess.Id})...");
            logixProcess.CloseMainWindow();
            if (!logixProcess.WaitForExit(30_000))
            {
                logger?.Status(acdPath, "Logix Designer did not close gracefully, forcing...");
                logixProcess.Kill();
                logixProcess.WaitForExit(10_000);
            }
            logger?.Status(acdPath, "Logix Designer closed.");
        }

        // Backup the file, same as logix designer would
        if (File.Exists(acdPath))
        {
            var backupFileName = GetAcdBackupFilePath(acdPath);
            File.Copy(acdPath, backupFileName);
        }

        // Move the restored ACD into place
        File.Move(tempAcdFile.Path, acdPath, true);
        logger?.Status(acdPath, "ACD file restored successfully.");

        // Reopen in Logix Designer using shell association (.ACD → Logix Designer)
        if (wasOpen)
        {
            logger?.Status(acdPath, "Reopening project in Logix Designer...");
            Process.Start(new ProcessStartInfo
            {
                FileName = acdPath,
                UseShellExecute = true,
            });
        }
    }

    static async Task ConvertL5xToAcd(string l5xFilePath, string acdFilePath)
    {
        Console.WriteLine($"Converting L5X file '{l5xFilePath}' to ACD file '{acdFilePath}'...");
        
        using LogixProject project = await LogixProject.OpenLogixProjectAsync(l5xFilePath, new StdOutEventLogger());
        await project.SaveAsAsync(acdFilePath, true);

        var fileBytes = new FileInfo(acdFilePath).Length;
        if (fileBytes == 0)
        {
            throw new OperationFailedException("Unable to save project: An unknown error has occured", acdFilePath);
        }
    }

    static Process? FindLogixDesignerProcess(string acdBaseName)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("LogixDesigner"))
            {
                try
                {
                    // Window title format: "Logix Designer - CDW5_MCM09 [1756-L83ES 36.11]*"
                    if (proc.MainWindowTitle.Contains(acdBaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return proc;
                    }
                }
                catch
                {
                    // Access denied to some process info — skip
                }
            }
        }
        catch
        {
            // GetProcessesByName can fail — not critical
        }

        return null;
    }

    static string GetAcdBackupFilePath(string acdFilePath)
    {
        var dir = Path.GetDirectoryName(acdFilePath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(acdFilePath);
        var ext = Path.GetExtension(acdFilePath).ToUpper();

        var userPart = $"{Environment.UserDomainName}.{Environment.UserName}";
        var backupPrefix = $"{baseName}.{userPart}.BAK";
        var searchPattern = $"{baseName}.{userPart}.BAK*.acd";

        // Regex to match: <baseName>.<userPart>.BAK###.acd
        var regex = new Regex($@"^{Regex.Escape(baseName)}\.{Regex.Escape(userPart)}\.BAK(\d{{3}})\.acd$", RegexOptions.IgnoreCase);

        int maxSeq = 0;
        foreach (var file in Directory.GetFiles(dir, searchPattern))
        {
            var fname = Path.GetFileName(file);
            var match = regex.Match(fname);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int seq) && seq > maxSeq)
            {
                maxSeq = seq;
            }
        }

        var nextSeq = maxSeq + 1;
        var backupFileName = $"{backupPrefix}{nextSeq:D3}{ext}";
        return Path.Combine(dir, backupFileName);
    }
}