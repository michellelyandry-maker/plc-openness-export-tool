using Siemens.Engineering;
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
            // Parse command-line arguments: --project "path" --output "folder"
            string argProjectPath = null;
            string argExportFolder = null;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--project") argProjectPath = args[i + 1];
                if (args[i] == "--output") argExportFolder = args[i + 1];
            }

            NonInteractiveMode = !string.IsNullOrWhiteSpace(argProjectPath) && !string.IsNullOrWhiteSpace(argExportFolder);

            try
            {
                RunExport(argProjectPath, argExportFolder);
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

            // Project path: from args if provided, else interactive prompt
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
            TiaPortal tia = AttachToPortal(projectFile);
            Project project = GetOrOpenProject(tia, projectFile);
            if (!NonInteractiveMode) Console.WriteLine($"Using project: {project.Name}");

            // Hardware export
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

            // Export folder: from args if provided, else interactive prompt
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

            // Block export via Openness
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

            SaveLastUsedPaths(new LastUsedPaths { ProjectPath = projectPath, ExportFolder = exportFolder });

            if (NonInteractiveMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    projectName = project.Name,
                    hardwareConfigPath = hardwarePath,
                    blocksFolder = blocksFolder,
                    blocksExported = exportedCount,
                    blocksSkipped = skippedCount
                }));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Success. Press Enter to exit.");
                Console.ReadLine();
            }
        }

        static TiaPortal AttachToPortal(FileInfo projectFile)
        {
            var processes = TiaPortal.GetProcesses();
            if (!processes.Any())
            {
                if (!NonInteractiveMode) Console.WriteLine("Started new TIA Portal instance.");
                return new TiaPortal(TiaPortalMode.WithUserInterface);
            }

            foreach (var process in processes)
            {
                TiaPortal attached = process.Attach();
                if (FindOpenProject(attached, projectFile) != null)
                {
                    if (!NonInteractiveMode) Console.WriteLine("Attached to running TIA Portal instance with this project.");
                    return attached;
                }
            }

            if (!NonInteractiveMode) Console.WriteLine("Attached to running TIA Portal instance.");
            return processes.First().Attach();
        }

        static Project GetOrOpenProject(TiaPortal tia, FileInfo projectFile)
        {
            Project alreadyOpen = FindOpenProject(tia, projectFile);
            if (alreadyOpen != null)
                return alreadyOpen;

            if (tia.Projects.Any())
            {
                Project other = tia.Projects.First();
                throw new InvalidOperationException(
                    $"Unable to open project '{projectFile.FullName}'. Another project is already open: '{other.Path?.FullName}'.");
            }

            return tia.Projects.Open(projectFile);
        }

        static Project FindOpenProject(TiaPortal tia, FileInfo projectFile)
        {
            foreach (Project project in tia.Projects)
            {
                if (project.Path != null &&
                    string.Equals(project.Path.FullName, projectFile.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return project;
                }
            }

            return null;
        }

        static void ExportBlockGroup(PlcBlockGroup group, string targetFolder, ref int exportedCount, ref int skippedCount)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                try
                {
                    string safeName = string.Join("_", block.Name.Split(Path.GetInvalidFileNameChars()));
                    var exportFile = new FileInfo(Path.Combine(targetFolder, $"{safeName}.xml"));

                    if (exportFile.Exists)
                    {
                        exportFile.Delete();
                    }

                    block.Export(exportFile, Siemens.Engineering.ExportOptions.WithDefaults);
                    exportedCount++;
                }
                catch
                {
                    skippedCount++;
                }
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