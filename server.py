import subprocess
import json
from mcp.server.fastmcp import FastMCP

mcp = FastMCP("plc-export")

EXE_PATH = r"C:\Users\Michelle Lyandry\source\repos\PLC_Openness_Export\PLC_Openness_Export\bin\Release\PLC_Openness_Export.exe"

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
    result = subprocess.run(
        [EXE_PATH, "--project", project_path, "--output", output_folder],
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

if __name__ == "__main__":
    mcp.run()