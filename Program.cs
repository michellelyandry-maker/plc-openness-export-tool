using Siemens.Engineering;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PLC_Openness_Export
{
    internal class Program
    {
        static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_used_paths.json");

        static void Main(string[] args)
        {
            try
            {
                RunExport();
            }
            catch (Siemens.Engineering.EngineeringSecurityException)
            {
                PrintPermissionError();
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is Siemens.Engineering.EngineeringSecurityException)
            {
                PrintPermissionError();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("=== Something went wrong ===");
                Console.WriteLine(ex.Message);
                Console.WriteLine();
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
            }
        }

        static void PrintPermissionError()
        {
            Console.WriteLine();
            Console.WriteLine("=== Permission Error ===");
            Console.WriteLine("Your Windows account isn't authorized to use TIA Portal Openness.");
            Console.WriteLine();
            Console.WriteLine("Fix: ask IT or an admin to run this command (as Administrator):");
            Console.WriteLine(@"  net localgroup ""Siemens TIA Openness"" ""YOUR_USERNAME"" /add");
            Console.WriteLine();
            Console.WriteLine("Then restart your computer and run this tool again.");
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
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
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Failed to load last-used paths: {ex.Message}");
            }
            return new LastUsedPaths();
        }

        static void SaveLastUsedPaths(LastUsedPaths paths)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(paths, options));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Failed to save last-used paths: {ex.Message}");
            }
        }

        static void RunExport()
        {
            var lastUsed = LoadLastUsedPaths();

            // Connecting to TIA Portal: either attach to a running instance or start a new one
            var instances = TiaPortal.GetProcesses();
            TiaPortal tia;

            if (instances.Any())
            {
                tia = instances.First().Attach();
                Console.WriteLine("Attached to running TIA Portal instance.");
            }
            else
            {
                tia = new TiaPortal(TiaPortalMode.WithUserInterface);
                Console.WriteLine("Started new TIA Portal instance.");
            }

            // Project selection: either use the last-used project or ask the user for a new one
            string projectPath = null;

            if (!string.IsNullOrWhiteSpace(lastUsed.ProjectPath) && File.Exists(lastUsed.ProjectPath))
            {
                Console.WriteLine();
                Console.WriteLine($"Using saved project: {lastUsed.ProjectPath}");
                Console.WriteLine("(To use a different project, type its path now, or just press Enter to continue.)");
                string overrideInput = Console.ReadLine()?.Trim().Trim('"');

                if (!string.IsNullOrWhiteSpace(overrideInput))
                {
                    projectPath = overrideInput;
                }
                else
                {
                    projectPath = lastUsed.ProjectPath;
                }
            }

            while (!File.Exists(projectPath))
            {
                Console.WriteLine();
                Console.WriteLine("Enter the full path to your .ap16 project file:");
                Console.WriteLine(@"(example: C:\Users\YourName\Documents\Automation\MyProject\MyProject.ap16)");
                projectPath = Console.ReadLine()?.Trim().Trim('"');

                if (!File.Exists(projectPath))
                {
                    Console.WriteLine();
                    Console.WriteLine("That file wasn't found. Please check the path and try again.");
                }
            }

            var projectFile = new FileInfo(projectPath);
            Project project = tia.Projects.Open(projectFile);

            Console.WriteLine($"Opened project: {project.Name}");

            // Walk through the devices and their modules, building a data structure to serialize
            var deviceList = new List<object>();

            foreach (Device device in project.Devices)
            {
                var deviceItems = new List<object>();

                foreach (DeviceItem item in device.DeviceItems)
                {
                    deviceItems.Add(BuildDeviceItemData(item));
                }

                deviceList.Add(new
                {
                    DeviceName = device.Name,
                    Modules = deviceItems
                });
            }

            // Convert the data structure to JSON
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(deviceList, jsonOptions);

            // Export the JSON to a file, either using the last-used export folder or asking the user for a new one
            Console.WriteLine();
            string exportFolder;

            if (!string.IsNullOrWhiteSpace(lastUsed.ExportFolder))
            {
                Console.WriteLine($"Using saved export folder: {lastUsed.ExportFolder}");
                Console.WriteLine("(To use a different folder, type it now, or just press Enter to continue.)");
                string overrideInput = Console.ReadLine()?.Trim().Trim('"');

                exportFolder = string.IsNullOrWhiteSpace(overrideInput)
                    ? lastUsed.ExportFolder
                    : overrideInput;
            }
            else
            {
                Console.WriteLine(@"Enter the folder to save the export to (or press Enter to use C:\PLC_Export):");
                string input = Console.ReadLine()?.Trim().Trim('"');
                exportFolder = string.IsNullOrWhiteSpace(input) ? @"C:\PLC_Export" : input;
            }

            Directory.CreateDirectory(exportFolder);
            string exportPath = Path.Combine(exportFolder, "hardware_config.json");
            File.WriteAllText(exportPath, json);

            // Save the last-used paths for next time
            SaveLastUsedPaths(new LastUsedPaths
            {
                ProjectPath = projectPath,
                ExportFolder = exportFolder
            });

            Console.WriteLine();
            Console.WriteLine($"Hardware config exported to: {exportPath}");
            Console.WriteLine("Success. Press Enter to exit.");
            Console.ReadLine();
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
                catch
                {
                    // Some attributes aren't readable for every item type - skip 
                }
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

