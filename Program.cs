using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.TextGraphicList;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PLC_Openness_Export
{
    internal class Program
    {
        static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_used_paths.json");

        static bool NonInteractiveMode = false;

        static void Main(string[] args)
        {
            string argProjectPath = null;
            string argExportFolder = null;
            string argImportBlockFile = null;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--project") argProjectPath = args[i + 1];
                if (args[i] == "--output") argExportFolder = args[i + 1];
                if (args[i] == "--import-block") argImportBlockFile = args[i + 1];
            }

            NonInteractiveMode = !string.IsNullOrWhiteSpace(argProjectPath) &&
                                  (!string.IsNullOrWhiteSpace(argExportFolder) || !string.IsNullOrWhiteSpace(argImportBlockFile));

            try
            {
                if (!string.IsNullOrWhiteSpace(argImportBlockFile))
                {
                    RunImportBlock(argProjectPath, argImportBlockFile);
                }
                else
                {
                    RunExport(argProjectPath, argExportFolder);
                }
            }
            catch (Siemens.Engineering.EngineeringSecurityException)
            {
                HandleFailure("Windows account not authorized for TIA Portal Openness. Add it to the 'Siemens TIA Openness' group and restart.");
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is Siemens.Engineering.EngineeringSecurityException)
            {
                HandleFailure("Windows account not authorized for TIA Portal Openness. Add it to the 'Siemens TIA Openness' group and restart.");
            }
            catch (Exception ex)
            {
                HandleFailure(ex.Message);
            }
        }

        static void HandleFailure(string message)
        {
            if (NonInteractiveMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, message }));
                Environment.Exit(1);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("=== Something went wrong ===");
                Console.WriteLine(message);
                Console.WriteLine();
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
            }
        }

        static TiaPortal ConnectToTia()
        {
            var instances = TiaPortal.GetProcesses();
            if (instances.Any())
            {
                if (!NonInteractiveMode) Console.WriteLine("Attached to running TIA Portal instance.");
                return instances.First().Attach();
            }
            else
            {
                if (!NonInteractiveMode) Console.WriteLine("Started new TIA Portal instance.");
                return new TiaPortal(TiaPortalMode.WithUserInterface);
            }
        }

        static void CollectCompileMessages(CompilerResultMessageComposition messages, List<string> collected)
        {
            if (messages == null)
                return;

            foreach (CompilerResultMessage message in messages)
            {
                if (message.State == CompilerResultState.Error || message.State == CompilerResultState.Warning)
                {
                    string path = string.IsNullOrWhiteSpace(message.Path) ? "" : message.Path + ": ";
                    collected.Add($"[{message.State}] {path}{message.Description}");
                }
                CollectCompileMessages(message.Messages, collected);
            }
        }

        static PlcBlock FindBlockByName(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return block;
                }
            }
            foreach (PlcBlockGroup subGroup in group.Groups)
            {
                var found = FindBlockByName(subGroup, name);
                if (found != null) return found;
            }
            return null;
        }

        static void RunImportBlock(string projectPath, string blockXmlPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            {
                throw new FileNotFoundException($"Project file not found: {projectPath}");
            }
            if (!File.Exists(blockXmlPath))
            {
                throw new FileNotFoundException($"Block XML file not found: {blockXmlPath}");
            }

            TiaPortal tia = ConnectToTia();

            var projectFile = new FileInfo(projectPath);
            Project project = tia.Projects.Open(projectFile);
            if (!NonInteractiveMode) Console.WriteLine($"Opened project: {project.Name}");

            var blockFile = new FileInfo(blockXmlPath);
            string intendedBlockName = Path.GetFileNameWithoutExtension(blockFile.Name);

            bool imported = false;
            string importedInto = null;
            string identityWarning = null;
            string backupPath = null;

            foreach (Device device in project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var softwareContainer = item.GetService<SoftwareContainer>();
                    if (softwareContainer?.Software is PlcSoftware plcSoftware)
                    {
                        PlcBlock existingBlock = FindBlockByName(plcSoftware.BlockGroup, intendedBlockName);

                        if (existingBlock == null)
                        {
                            identityWarning =
                                $"No existing block named '{intendedBlockName}' was found in this project. " +
                                "This may mean the XML belongs to a different project, or this is a new block " +
                                "being added rather than an existing one being restored. Proceeding anyway.";
                        }
                        else
                        {
                            // Ensure blocks are in a consistent state before attempting to export one
                            try
                            {
                                var preCompileService = plcSoftware.GetService<ICompilable>();
                                preCompileService?.Compile();
                            }
                            catch
                            {
                                // If pre-compile fails, we still attempt the backup below;
                                // the backup's own try/catch will report if it fails too.
                            }

                            // --- Backup the current block before overwriting ---
                            try
                            {
                                string exportRoot = Directory.GetParent(blockFile.DirectoryName)?.FullName
                                                    ?? blockFile.DirectoryName;
                                string backupFolder = Path.Combine(exportRoot, "Backups");
                                Directory.CreateDirectory(backupFolder);

                                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                                var backupFile = new FileInfo(
                                    Path.Combine(backupFolder, $"{intendedBlockName}_{timestamp}.xml"));

                                existingBlock.Export(backupFile, Siemens.Engineering.ExportOptions.WithDefaults);
                                backupPath = backupFile.FullName;
                            }
                            catch (Exception ex)
                            {
                                identityWarning = (identityWarning ?? "") +
                                    $" [Backup of current block failed: {ex.Message}]";
                            }
                        }

                        // --- The actual import ---
                        plcSoftware.BlockGroup.Blocks.Import(
                            blockFile,
                            Siemens.Engineering.ImportOptions.Override
                        );
                        imported = true;
                        importedInto = plcSoftware.Name;

                        project.Save();

                        // --- Compile check after import ---
                        string compileState = "not-checked";
                        var compileMessages = new List<string>();

                        int compileErrorCount = 0;
                        int compileWarningCount = 0;
                        try
                        {
                            var compileService = plcSoftware.GetService<ICompilable>();
                            if (compileService != null)
                            {
                                var compileResult = compileService.Compile();
                                compileState = compileResult.State.ToString();
                                compileErrorCount = compileResult.ErrorCount;
                                compileWarningCount = compileResult.WarningCount;
                                CollectCompileMessages(compileResult.Messages, compileMessages);
                            }
                            else
                            {
                                compileState = "compile-service-unavailable";
                                compileErrorCount = 1;
                                compileMessages.Add("PLC software did not expose ICompilable; compile was skipped.");
                            }
                        }
                        catch (Exception ex)
                        {
                            compileState = "compile-check-failed";
                            compileErrorCount = 1;
                            compileMessages.Add(ex.Message);
                        }

                        bool success = compileErrorCount == 0 &&
                                       !string.Equals(compileState, "Error", StringComparison.OrdinalIgnoreCase);

                        if (NonInteractiveMode)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new
                            {
                                success = success,
                                action = "import-block",
                                projectName = project.Name,
                                importedFile = blockXmlPath,
                                importedInto = importedInto,
                                identityWarning = identityWarning,
                                backupOfPreviousVersion = backupPath,
                                compileState = compileState,
                                compileErrorCount = compileErrorCount,
                                compileWarningCount = compileWarningCount,
                                compileMessages = compileMessages
                            }));
                            if (!success)
                                Environment.Exit(1);
                        }
                        else
                        {
                            Console.WriteLine(success
                                ? $"Block imported from: {blockXmlPath}"
                                : $"Block imported from: {blockXmlPath}, but compile reported errors.");
                            if (backupPath != null)
                                Console.WriteLine($"Previous version backed up to: {backupPath}");
                            if (identityWarning != null)
                                Console.WriteLine($"WARNING: {identityWarning}");
                            Console.WriteLine($"Compile state after import: {compileState} ({compileErrorCount} error(s), {compileWarningCount} warning(s))");
                            if (compileMessages.Count > 0)
                            {
                                Console.WriteLine("Compile messages:");
                                foreach (var m in compileMessages) Console.WriteLine($"  - {m}");
                            }
                            Console.WriteLine("Project saved.");
                            Console.WriteLine("Press Enter to exit.");
                            Console.ReadLine();
                        }

                        return;
                    }
                }
            }

            if (!imported)
            {
                throw new InvalidOperationException("No PLC software container found in this project to import into.");
            }
        }

        class LastUsedPaths
        {
            public string ProjectPath { get; set; }
            public string ExportFolder { get; set; }
        }

        static LastUsedPaths LoadLastUsedPaths()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<LastUsedPaths>(json);
                }
            }
            catch { }
            return new LastUsedPaths();
        }

        static void SaveLastUsedPaths(LastUsedPaths paths)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(paths, options));
            }
            catch { }
        }

        static void RunExport(string argProjectPath, string argExportFolder)
        {
            var lastUsed = LoadLastUsedPaths();
            TiaPortal tia = ConnectToTia();

            string projectPath = argProjectPath;

            if (string.IsNullOrWhiteSpace(projectPath))
            {
                if (!string.IsNullOrWhiteSpace(lastUsed.ProjectPath) && File.Exists(lastUsed.ProjectPath))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Using saved project: {lastUsed.ProjectPath}");
                    Console.WriteLine("(To use a different project, type its path now, or just press Enter to continue.)");
                    string overrideInput = Console.ReadLine()?.Trim().Trim('"');
                    projectPath = string.IsNullOrWhiteSpace(overrideInput) ? lastUsed.ProjectPath : overrideInput;
                }

                while (!File.Exists(projectPath))
                {
                    Console.WriteLine();
                    Console.WriteLine("Enter the full path to your .ap16 project file:");
                    projectPath = Console.ReadLine()?.Trim().Trim('"');
                    if (!File.Exists(projectPath))
                    {
                        Console.WriteLine("That file wasn't found. Please check the path and try again.");
                    }
                }
            }
            else if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException($"Project file not found: {projectPath}");
            }

            var projectFile = new FileInfo(projectPath);
            Project project = tia.Projects.Open(projectFile);
            if (!NonInteractiveMode) Console.WriteLine($"Opened project: {project.Name}");

            var deviceList = new List<object>();
            foreach (Device device in project.Devices)
            {
                var deviceItems = new List<object>();
                foreach (DeviceItem item in device.DeviceItems)
                {
                    deviceItems.Add(BuildDeviceItemData(item));
                }
                deviceList.Add(new { DeviceName = device.Name, Modules = deviceItems });
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string hardwareJson = JsonSerializer.Serialize(deviceList, jsonOptions);

            string exportFolder = argExportFolder;

            if (string.IsNullOrWhiteSpace(exportFolder))
            {
                Console.WriteLine();
                if (!string.IsNullOrWhiteSpace(lastUsed.ExportFolder))
                {
                    Console.WriteLine($"Using saved export folder: {lastUsed.ExportFolder}");
                    Console.WriteLine("(To use a different folder, type it now, or just press Enter to continue.)");
                    string overrideInput = Console.ReadLine()?.Trim().Trim('"');
                    exportFolder = string.IsNullOrWhiteSpace(overrideInput) ? lastUsed.ExportFolder : overrideInput;
                }
                else
                {
                    Console.WriteLine(@"Enter the folder to save the export to (or press Enter to use C:\PLC_Export):");
                    string input = Console.ReadLine()?.Trim().Trim('"');
                    exportFolder = string.IsNullOrWhiteSpace(input) ? @"C:\PLC_Export" : input;
                }
            }

            Directory.CreateDirectory(exportFolder);
            string hardwarePath = Path.Combine(exportFolder, "hardware_config.json");
            File.WriteAllText(hardwarePath, hardwareJson);
            if (!NonInteractiveMode) Console.WriteLine($"Hardware config exported to: {hardwarePath}");

            string blocksFolder = Path.Combine(exportFolder, "Blocks");
            Directory.CreateDirectory(blocksFolder);

            int exportedCount = 0;
            int skippedCount = 0;

            foreach (Device device in project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var softwareContainer = item.GetService<SoftwareContainer>();
                    if (softwareContainer?.Software is PlcSoftware plcSoftware)
                    {
                        ExportBlockGroup(plcSoftware.BlockGroup, blocksFolder, ref exportedCount, ref skippedCount);
                    }
                }
            }

            if (!NonInteractiveMode)
                Console.WriteLine($"Program blocks exported to: {blocksFolder} ({exportedCount} exported, {skippedCount} skipped)");

            string hmiRoot = Path.Combine(exportFolder, "Hmi");
            int hmiTargets = 0;
            int hmiExported = 0;
            int hmiSkipped = 0;

            foreach (Device device in project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var softwareContainer = item.GetService<SoftwareContainer>();
                    if (softwareContainer?.Software is HmiTarget hmiTarget)
                    {
                        hmiTargets++;
                        string targetFolder = Path.Combine(hmiRoot, SafeFileName(hmiTarget.Name));
                        ExportHmiTarget(hmiTarget, targetFolder, ref hmiExported, ref hmiSkipped);
                    }
                }
            }

            if (!NonInteractiveMode)
            {
                if (hmiTargets == 0)
                    Console.WriteLine("No HMI / WinCC device found in this project. Nothing to export under Hmi/.");
                else
                    Console.WriteLine($"HMI exported to: {hmiRoot} ({hmiTargets} device(s), {hmiExported} exported, {hmiSkipped} skipped)");
            }

            SaveLastUsedPaths(new LastUsedPaths { ProjectPath = projectPath, ExportFolder = exportFolder });

            if (NonInteractiveMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    action = "export",
                    projectName = project.Name,
                    hardwareConfigPath = hardwarePath,
                    blocksFolder = blocksFolder,
                    blocksExported = exportedCount,
                    blocksSkipped = skippedCount,
                    hmiFolder = hmiTargets > 0 ? hmiRoot : null,
                    hmiTargets = hmiTargets,
                    hmiExported = hmiExported,
                    hmiSkipped = hmiSkipped
                }));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Success. Press Enter to exit.");
                Console.ReadLine();
            }
        }

        static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unnamed";
            return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        }

        static void ExportXml(string folder, string name, Action<FileInfo> export, ref int exportedCount, ref int skippedCount)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var exportFile = new FileInfo(Path.Combine(folder, SafeFileName(name) + ".xml"));
                if (exportFile.Exists)
                    exportFile.Delete();
                export(exportFile);
                exportedCount++;
            }
            catch
            {
                skippedCount++;
            }
        }

        static void ExportHmiTarget(HmiTarget hmi, string targetFolder, ref int exportedCount, ref int skippedCount)
        {
            ExportScreenTree(hmi.ScreenFolder, Path.Combine(targetFolder, "Screens"), ref exportedCount, ref skippedCount);
            ExportPopupTree(hmi.ScreenPopupFolder, Path.Combine(targetFolder, "Popups"), ref exportedCount, ref skippedCount);
            ExportTemplateTree(hmi.ScreenTemplateFolder, Path.Combine(targetFolder, "Templates"), ref exportedCount, ref skippedCount);
            ExportTagTree(hmi.TagFolder, Path.Combine(targetFolder, "Tags"), ref exportedCount, ref skippedCount);
            ExportScriptTree(hmi.VBScriptFolder, Path.Combine(targetFolder, "Scripts"), ref exportedCount, ref skippedCount);

            if (hmi.ScreenGlobalElements != null)
            {
                ExportXml(targetFolder, "ScreenGlobalElements",
                    file => hmi.ScreenGlobalElements.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            if (hmi.ScreenOverview != null)
            {
                ExportXml(targetFolder, "ScreenOverview",
                    file => hmi.ScreenOverview.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (Connection connection in hmi.Connections)
            {
                ExportXml(Path.Combine(targetFolder, "Connections"), connection.Name,
                    file => connection.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (TextList textList in hmi.TextLists)
            {
                ExportXml(Path.Combine(targetFolder, "TextLists"), textList.Name,
                    file => textList.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (GraphicList graphicList in hmi.GraphicLists)
            {
                ExportXml(Path.Combine(targetFolder, "GraphicLists"), graphicList.Name,
                    file => graphicList.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportScreenTree(ScreenFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (Screen screen in folder.Screens)
            {
                ExportXml(directory, screen.Name,
                    file => screen.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (ScreenUserFolder subFolder in folder.Folders)
            {
                ExportScreenTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportPopupTree(ScreenPopupFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                ExportXml(directory, popup.Name,
                    file => popup.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (ScreenPopupUserFolder subFolder in folder.Folders)
            {
                ExportPopupUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportPopupUserTree(ScreenPopupUserFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                ExportXml(directory, popup.Name,
                    file => popup.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (ScreenPopupUserFolder subFolder in folder.Folders)
            {
                ExportPopupUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportTemplateTree(ScreenTemplateFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (ScreenTemplate template in folder.ScreenTemplates)
            {
                ExportXml(directory, template.Name,
                    file => template.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (ScreenTemplateUserFolder subFolder in folder.Folders)
            {
                ExportTemplateUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportTemplateUserTree(ScreenTemplateUserFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (ScreenTemplate template in folder.ScreenTemplates)
            {
                ExportXml(directory, template.Name,
                    file => template.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (ScreenTemplateUserFolder subFolder in folder.Folders)
            {
                ExportTemplateUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportTagTree(TagFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (TagTable table in folder.TagTables)
            {
                ExportXml(directory, table.Name,
                    file => table.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (TagUserFolder subFolder in folder.Folders)
            {
                ExportTagTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportScriptTree(VBScriptFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (VBScript script in folder.VBScripts)
            {
                ExportXml(directory, script.Name,
                    file => script.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (VBScriptUserFolder subFolder in folder.Folders)
            {
                ExportScriptUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportScriptUserTree(VBScriptUserFolder folder, string directory, ref int exportedCount, ref int skippedCount)
        {
            if (folder == null)
                return;

            foreach (VBScript script in folder.VBScripts)
            {
                ExportXml(directory, script.Name,
                    file => script.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (VBScriptUserFolder subFolder in folder.Folders)
            {
                ExportScriptUserTree(subFolder, Path.Combine(directory, SafeFileName(subFolder.Name)),
                    ref exportedCount, ref skippedCount);
            }
        }

        static void ExportBlockGroup(PlcBlockGroup group, string targetFolder, ref int exportedCount, ref int skippedCount)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                ExportXml(targetFolder, block.Name,
                    file => block.Export(file, ExportOptions.WithDefaults),
                    ref exportedCount, ref skippedCount);
            }

            foreach (PlcBlockGroup subGroup in group.Groups)
            {
                ExportBlockGroup(subGroup, targetFolder, ref exportedCount, ref skippedCount);
            }
        }

        static object BuildDeviceItemData(DeviceItem item)
        {
            var attributes = new Dictionary<string, string>();
            foreach (var attrInfo in item.GetAttributeInfos())
            {
                try
                {
                    var value = item.GetAttribute(attrInfo.Name);
                    attributes[attrInfo.Name] = value?.ToString() ?? "";
                }
                catch { }
            }

            var children = new List<object>();
            foreach (DeviceItem child in item.DeviceItems)
            {
                children.Add(BuildDeviceItemData(child));
            }

            return new
            {
                ModuleName = item.Name,
                TypeIdentifier = item.TypeIdentifier,
                Attributes = attributes,
                SubModules = children
            };
        }
    }
}