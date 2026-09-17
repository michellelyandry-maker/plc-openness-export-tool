using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;

namespace PLC_Openness_Export
{
    internal static class PlcEdits
    {
        public static Project OpenProject(TiaPortal tia, string projectPath)
        {
            var projectFile = new FileInfo(projectPath);
            foreach (Project open in tia.Projects)
            {
                if (open.Path != null &&
                    string.Equals(open.Path.FullName, projectFile.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return open;
                }
            }

            if (tia.Projects.Any())
            {
                throw new InvalidOperationException(
                    "A different TIA Portal project is already open. Close it first.");
            }

            return tia.Projects.Open(projectFile);
        }

        public static Device FindDevice(Project project, string deviceName)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                Device named = project.Devices.Find(deviceName);
                if (named != null)
                    return named;
                foreach (Device device in project.Devices)
                {
                    if (string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                        return device;
                }
                throw new InvalidOperationException("Device not found: " + deviceName);
            }

            Device first = project.Devices.FirstOrDefault();
            if (first == null)
                throw new InvalidOperationException("This project has no devices.");
            return first;
        }

        public static PlcSoftware FindPlcSoftware(Project project)
        {
            foreach (Device device in project.Devices)
            {
                PlcSoftware software = FindPlcSoftware(device);
                if (software != null)
                    return software;
            }
            throw new InvalidOperationException("No PLC software found in this project.");
        }

        static PlcSoftware FindPlcSoftware(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                PlcSoftware software = FindPlcSoftware(item);
                if (software != null)
                    return software;
            }
            return null;
        }

        static PlcSoftware FindPlcSoftware(DeviceItem item)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware plc)
                return plc;
            foreach (DeviceItem child in item.DeviceItems)
            {
                PlcSoftware software = FindPlcSoftware(child);
                if (software != null)
                    return software;
            }
            return null;
        }

        public static string AddHardware(
            Project project,
            string typeIdentifier,
            string name,
            int position,
            string deviceName,
            bool asNewStation)
        {
            if (string.IsNullOrWhiteSpace(typeIdentifier))
                throw new ArgumentException("type_identifier is required (for example OrderNumber:6ES7 221-3BD30-0XB0/V1.0).");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name is required.");
            if (!typeIdentifier.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase) &&
                !typeIdentifier.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
            {
                typeIdentifier = "OrderNumber:" + typeIdentifier;
            }

            if (asNewStation)
            {
                Device created = project.Devices.CreateWithItem(typeIdentifier, name, name);
                return created.Name;
            }

            Device device = FindDevice(project, deviceName);
            DeviceItem plugged = TryPlug(device, typeIdentifier, name, position);
            if (plugged == null)
            {
                throw new InvalidOperationException(
                    "Could not plug that module. Check the order number, name, slot/position, and that the station is an S7-1200/1500 rack that accepts it.");
            }
            return plugged.Name;
        }

        static DeviceItem TryPlug(Device device, string typeIdentifier, string name, int position)
        {
            DeviceItem fromDevice = TryPlugOn(device, typeIdentifier, name, position);
            if (fromDevice != null)
                return fromDevice;

            foreach (DeviceItem item in device.DeviceItems)
            {
                DeviceItem plugged = TryPlugRecursive(item, typeIdentifier, name, position);
                if (plugged != null)
                    return plugged;
            }
            return null;
        }

        static DeviceItem TryPlugRecursive(DeviceItem item, string typeIdentifier, string name, int position)
        {
            DeviceItem plugged = TryPlugOn(item, typeIdentifier, name, position);
            if (plugged != null)
                return plugged;
            foreach (DeviceItem child in item.DeviceItems)
            {
                DeviceItem nested = TryPlugRecursive(child, typeIdentifier, name, position);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        static DeviceItem TryPlugOn(HardwareObject hardware, string typeIdentifier, string name, int position)
        {
            try
            {
                if (hardware.CanPlugNew(typeIdentifier, name, position))
                    return hardware.PlugNew(typeIdentifier, name, position);
            }
            catch
            {
            }
            return null;
        }

        public static string AddConnection(Project project, string subnetName, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(subnetName))
                subnetName = "PN/IE_1";

            Device device = FindDevice(project, deviceName);
            Node node = FindFirstNode(device);
            if (node == null)
                throw new InvalidOperationException("No Ethernet/PROFINET port found on that device.");

            Subnet existing = project.Subnets.Find(subnetName);
            if (existing != null)
            {
                node.ConnectToSubnet(existing);
                return existing.Name;
            }

            Subnet created = node.CreateAndConnectToSubnet(subnetName);
            return created != null ? created.Name : subnetName;
        }

        static Node FindFirstNode(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                Node node = FindFirstNode(item);
                if (node != null)
                    return node;
            }
            return null;
        }

        static Node FindFirstNode(DeviceItem item)
        {
            NetworkInterface nic = item.GetService<NetworkInterface>();
            if (nic != null && nic.Nodes.Count > 0)
                return nic.Nodes[0];
            foreach (DeviceItem child in item.DeviceItems)
            {
                Node node = FindFirstNode(child);
                if (node != null)
                    return node;
            }
            return null;
        }

        public static string AddCodeBlock(
            Project project,
            string blockType,
            string name,
            string language,
            int number)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name is required.");

            PlcSoftware plc = FindPlcSoftware(project);
            if (plc.BlockGroup.Blocks.Find(name) != null)
                throw new InvalidOperationException("A block named '" + name + "' already exists.");

            ProgrammingLanguage lang = ParseLanguage(language);
            string type = (blockType ?? "FB").Trim().ToUpperInvariant();
            bool autoNumber = number <= 0;
            int blockNumber = autoNumber ? 1 : number;

            if (type == "FB")
            {
                plc.BlockGroup.Blocks.CreateFB(name, autoNumber, blockNumber, lang);
                return name;
            }

            string xml = BuildBlockXml(type, name, lang.ToString(), autoNumber, blockNumber);
            ImportBlockXml(plc, xml);
            return name;
        }

        public static string AddDataBlock(Project project, string name, int number)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name is required.");

            PlcSoftware plc = FindPlcSoftware(project);
            if (plc.BlockGroup.Blocks.Find(name) != null)
                throw new InvalidOperationException("A block named '" + name + "' already exists.");

            bool autoNumber = number <= 0;
            int blockNumber = autoNumber ? 1 : number;
            string xml = BuildBlockXml("DB", name, "DB", autoNumber, blockNumber);
            ImportBlockXml(plc, xml);
            return name;
        }

        static ProgrammingLanguage ParseLanguage(string language)
        {
            string value = (language ?? "LAD").Trim().ToUpperInvariant();
            if (value == "SCL")
                return ProgrammingLanguage.SCL;
            if (value == "FBD")
                return ProgrammingLanguage.FBD;
            if (value == "STL")
                return ProgrammingLanguage.STL;
            return ProgrammingLanguage.LAD;
        }

        static void ImportBlockXml(PlcSoftware plc, string xml)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "plc_openness_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tempPath, xml, Encoding.UTF8);
            try
            {
                plc.BlockGroup.Blocks.Import(new FileInfo(tempPath), ImportOptions.Override);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        static string XmlName(string name)
        {
            return SecurityElement.Escape(name) ?? name;
        }

        static string BuildBlockXml(string type, string name, string language, bool autoNumber, int number)
        {
            string safe = XmlName(name);
            string auto = autoNumber ? "true" : "false";
            string header =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<Document><Engineering version=\"V16\" />" +
                "<DocumentInfo><Created>" + DateTime.UtcNow.ToString("o") + "</Created>" +
                "<ExportSetting>WithDefaults</ExportSetting></DocumentInfo>";

            if (type == "DB")
            {
                return header +
                    "<SW.Blocks.GlobalDB ID=\"0\"><AttributeList>" +
                    "<AutoNumber>" + auto + "</AutoNumber>" +
                    "<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v4\">" +
                    "<Section Name=\"Static\" /></Sections></Interface>" +
                    "<MemoryLayout>Optimized</MemoryLayout>" +
                    "<Name>" + safe + "</Name><Number>" + number + "</Number>" +
                    "<ProgrammingLanguage>DB</ProgrammingLanguage>" +
                    "</AttributeList></SW.Blocks.GlobalDB></Document>";
            }

            if (type == "FC")
            {
                return header +
                    "<SW.Blocks.FC ID=\"0\"><AttributeList>" +
                    "<AutoNumber>" + auto + "</AutoNumber>" +
                    "<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v4\">" +
                    "<Section Name=\"Input\" /><Section Name=\"Output\" /><Section Name=\"InOut\" />" +
                    "<Section Name=\"Temp\" /><Section Name=\"Constant\" /><Section Name=\"Return\" />" +
                    "</Sections></Interface>" +
                    "<MemoryLayout>Optimized</MemoryLayout>" +
                    "<Name>" + safe + "</Name><Number>" + number + "</Number>" +
                    "<ProgrammingLanguage>" + language + "</ProgrammingLanguage>" +
                    "</AttributeList><ObjectList>" +
                    "<SW.Blocks.CompileUnit ID=\"1\" CompositionName=\"CompileUnits\"><AttributeList>" +
                    "<NetworkSource /><ProgrammingLanguage>" + language + "</ProgrammingLanguage>" +
                    "</AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
            }

            if (type == "OB")
            {
                return header +
                    "<SW.Blocks.OB ID=\"0\"><AttributeList>" +
                    "<AutoNumber>" + auto + "</AutoNumber>" +
                    "<Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v4\">" +
                    "<Section Name=\"Input\" /><Section Name=\"Temp\" /><Section Name=\"Constant\" />" +
                    "</Sections></Interface>" +
                    "<MemoryLayout>Optimized</MemoryLayout>" +
                    "<Name>" + safe + "</Name><Number>" + number + "</Number>" +
                    "<ProgrammingLanguage>" + language + "</ProgrammingLanguage>" +
                    "<SecondaryType>ProgramCycle</SecondaryType>" +
                    "</AttributeList><ObjectList>" +
                    "<SW.Blocks.CompileUnit ID=\"1\" CompositionName=\"CompileUnits\"><AttributeList>" +
                    "<NetworkSource /><ProgrammingLanguage>" + language + "</ProgrammingLanguage>" +
                    "</AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>";
            }

            throw new ArgumentException("Unsupported block type '" + type + "'. Use FB, FC, OB, or DB.");
        }
    }
}
