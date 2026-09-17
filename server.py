import subprocess
import json
import os
from mcp.server.fastmcp import FastMCP

mcp = FastMCP("plc-export")

EXE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bin", "Release", "PLC_Openness_Export.exe")


def _run_exe(arguments):
    result = subprocess.run(
        [EXE_PATH] + arguments,
        capture_output=True,
        text=True,
        timeout=600
    )
    if result.returncode != 0:
        return {"success": False, "error": result.stderr or result.stdout}
    try:
        return json.loads(result.stdout.strip())
    except json.JSONDecodeError:
        return {"success": False, "error": f"Unexpected output: {result.stdout}"}


@mcp.tool()
def export_plc_project(project_path: str, output_folder: str) -> dict:
    """
    Exports a TIA Portal project's hardware configuration and program blocks
    using the Openness API. Requires TIA Portal to be closed or already open
    with this exact project. Returns a summary of what was exported.

    Args:
        project_path: Full path to the .ap16 project file.
        output_folder: Folder to export hardware_config.json and Blocks/ into.
    """
    return _run_exe(["--project", project_path, "--output", output_folder])


@mcp.tool()
def setup_tia_project(
    target_directory: str,
    name: str,
    cpu_type_identifier: str = "OrderNumber:6ES7 214-1AG40-0XB0/V4.4",
    station_name: str = "S7-1200 station_1",
    plc_name: str = "PLC_1",
) -> dict:
    """
    Creates a new TIA Portal V16 project from Cursor, adds a default S7-1200
    CPU 1214C DC/DC/DC, writes .cursor/mcp.json and .gitignore, runs git init,
    and exports hardware/blocks. Close any other open TIA project first.

    Args:
        target_directory: Parent folder that will contain the new project folder.
        name: TIA project name (also the folder and .ap16 file name).
        cpu_type_identifier: Openness type identifier for the CPU to insert.
        station_name: Hardware station / device name.
        plc_name: PLC name inside the station.
    """
    return _run_exe([
        "--create-project",
        "--target-directory", target_directory,
        "--name", name,
        "--cpu-type-identifier", cpu_type_identifier,
        "--station-name", station_name,
        "--plc-name", plc_name,
    ])


def _project_args(project_path, action, extra):
    arguments = ["--project", project_path, "--action", action]
    arguments.extend(extra)
    return _run_exe(arguments)


@mcp.tool()
def add_plc_hardware(
    project_path: str,
    type_identifier: str,
    name: str,
    position: int = 2,
    device_name: str = "",
    as_new_station: bool = False,
) -> dict:
    """
    Adds hardware to an existing TIA Portal V16 project, then saves and exports.
    Close any other open TIA project first. Default is plugging a module into
    an existing station (for example a DI/DQ board). Set as_new_station to add
    a new PLC/station instead.

    Args:
        project_path: Full path to the .ap16 project file.
        type_identifier: Catalog order number, e.g. 6ES7 221-3BD30-0XB0/V1.0
            or OrderNumber:6ES7 221-3BD30-0XB0/V1.0.
        name: Name of the new module or station.
        position: Rack/slot position when plugging into an existing station.
        device_name: Existing station to plug into. Empty uses the first device.
        as_new_station: True to create a new device/station instead of plugging.
    """
    extra = [
        "--type-identifier", type_identifier,
        "--name", name,
        "--position", str(position),
    ]
    if device_name:
        extra.extend(["--device-name", device_name])
    if as_new_station:
        extra.append("--as-new-station")
    return _project_args(project_path, "add-hardware", extra)


@mcp.tool()
def add_plc_hmi(
    project_path: str,
    name: str = "HMI_1",
    type_identifier: str = "",
    subnet_name: str = "PN/IE_1",
    plc_device_name: str = "",
) -> dict:
    """
    Adds a SIMATIC HMI panel (KTP400 Basic by default) to an existing TIA Portal
    V16 project, connects it and the PLC to a PN/IE subnet, then saves and
    exports. Close any other open TIA project first.

    Args:
        project_path: Full path to the .ap16 project file.
        name: HMI station name.
        type_identifier: Optional catalog order number. Empty tries common
            KTP400/KTP700 Basic panels for V16.
        subnet_name: Subnet to share with the PLC.
        plc_device_name: PLC station to put on the same subnet. Empty uses the first device.
    """
    extra = ["--name", name, "--subnet-name", subnet_name]
    if type_identifier:
        extra.extend(["--type-identifier", type_identifier])
    if plc_device_name:
        extra.extend(["--device-name", plc_device_name])
    return _project_args(project_path, "add-hmi", extra)


@mcp.tool()
def add_plc_connection(
    project_path: str,
    subnet_name: str = "PN/IE_1",
    device_name: str = "",
) -> dict:
    """
    Connects the first Ethernet/PROFINET port of a device to a PN/IE subnet,
    creating the subnet if needed. Then saves and exports the project.

    Args:
        project_path: Full path to the .ap16 project file.
        subnet_name: Subnet name, typically PN/IE_1.
        device_name: Station to connect. Empty uses the first device.
    """
    extra = ["--subnet-name", subnet_name]
    if device_name:
        extra.extend(["--device-name", device_name])
    return _project_args(project_path, "add-connection", extra)


@mcp.tool()
def add_plc_block(
    project_path: str,
    name: str,
    block_type: str = "FB",
    language: str = "LAD",
    number: int = 0,
) -> dict:
    """
    Adds an empty code block (FB, FC, or OB) to the PLC, then saves and exports.
    FB uses Openness CreateFB. FC and OB are created via Simatic ML import.
    Language: LAD, FBD, SCL, or STL.

    Args:
        project_path: Full path to the .ap16 project file.
        name: Block name.
        block_type: FB, FC, or OB.
        language: LAD, FBD, SCL, or STL.
        number: Block number. 0 means auto-number.
    """
    extra = [
        "--name", name,
        "--block-type", block_type,
        "--language", language,
        "--number", str(number),
    ]
    return _project_args(project_path, "add-block", extra)


@mcp.tool()
def add_plc_data_block(
    project_path: str,
    name: str,
    number: int = 0,
) -> dict:
    """
    Adds an empty global data block to the PLC, then saves and exports.

    Args:
        project_path: Full path to the .ap16 project file.
        name: Data block name.
        number: DB number. 0 means auto-number.
    """
    extra = ["--name", name, "--number", str(number)]
    return _project_args(project_path, "add-data-block", extra)


if __name__ == "__main__":
    mcp.run()