# PLC Hardware & Block Export Tool

Exports both the hardware configuration (CPU, modules, network settings)
and the program blocks (OBs, FBs, data blocks) from a TIA Portal project,
using the Openness API — so everything can be tracked in Git as readable,
diffable files.

- Hardware exports to `hardware_config.json`
- Program blocks export to XML files in a `Blocks/` folder

## One-time setup (per computer)

Before this tool will run, four things need to be true on your machine:

1. **TIA Portal Openness must be installed.**
   This normally comes bundled with TIA Portal itself. To check, look for:

C:\Program Files\Siemens\Automation\Portal V16\PublicAPI\V16\Siemens.Engineering.dll

   If that file exists, you're set.

2. **.NET Framework 4.7.2 or later must be installed.**
   Most Windows machines already have this. If the tool fails to launch at
   all, this is the first thing to check.

3. **Your Windows account must be a member of the local
   "Siemens TIA Openness" security group.**
   This is the step almost everyone gets stuck on — it requires admin
   rights to set up. If you don't see this group, or don't have admin
   rights, ask IT to do this for you.

   **To add yourself (requires an Administrator Command Prompt):**

net localgroup "Siemens TIA Openness" "YOUR_WINDOWS_USERNAME" /add

   Not sure of your exact username? Run `whoami` first — it prints your
   account name in `COMPUTERNAME\username` format.

   **After running this command, restart your computer.** The group
   membership won't take effect until you do.

4. **Git must be installed**, with your name and email configured so
   commits are attributed to you correctly:

git config --global user.name "Your Name"
git config --global user.email "your.email@company.com"

   Without this, Git may use a generic or missing identity, which makes
   it hard to tell who made which change later.

If you skip step 3, the tool will still run but will fail with a
permission error the moment it tries to talk to TIA Portal. The tool
will tell you this directly and print the exact command above if that
happens.

## A quirk to know about: TIA Portal's own "Git Commit" popup

If you ever export a block manually through TIA Portal's Version Control
Interface (VCI) instead of using this tool, TIA Portal may show its own
**"Git Commit"** dialog asking for a commit message. **Click Cancel on
this dialog.** This project manages Git manually (or via the MCP
integration below), not through VCI's built-in Git feature.

## How to run it manually

1. Make sure your TIA Portal project is **closed** (or open in the same
   TIA Portal instance you intend to use — don't have two different
   projects open when you run this).
2. Run `PLC_Openness_Export.exe` (or `dotnet run` / F5 if running from
   source) with no arguments for interactive mode.
3. If you've used the tool before, it will show you the last project path
   and export folder you used — press Enter to reuse them, or type a new
   path to use a different project.
4. If this is your first time, you'll be asked to enter:
   - The full path to your project's `.ap16` file
   - The folder to export into (this should be your PLC project's own
     Git-tracked folder — see the `plc-version-control-template` repo
     for setting that up)
5. Wait for TIA Portal to open/attach and the project to load.
6. When you see `Success. Press Enter to exit.`, the export is done —
   both `hardware_config.json` and a `Blocks/` folder will be in your
   chosen export folder.

## Running it non-interactively (command-line arguments)

For scripting or MCP use, pass the paths directly and it will run without
any prompts, printing a single line of JSON when done:

PLC_Openness_Export.exe --project "C:\path\to\Project.ap16" --output "C:\path\to\export\folder"


Example success output:
```json
{"success":true,"projectName":"MyProject","hardwareConfigPath":"...","blocksFolder":"...","blocksExported":3,"blocksSkipped":0}
```

## What to do with the exported files

These files are meant to be committed to Git, in your project's own
repository:

git add .
git commit -m "Describe what changed, e.g. 'Added digital input module'"
git push


Re-run this tool any time hardware or program blocks change, then repeat
the commit step. `git diff` will show exactly what changed between
versions. The tool automatically overwrites previous block exports on
each run, so re-runs always reflect the current project state.

## Optional: Use this tool from Cursor (AI-assisted, no manual commands)

**Clone this repo to exactly `C:\PLC_Tools\plc-openness-export-tool`** so
the pre-configured MCP setup in project repos (from
`plc-version-control-template`) works automatically with no path editing.

Instead of running the `.exe` directly, you can wire this tool into
Cursor so you can trigger exports by just asking in plain English (e.g.
"export this PLC project" or "what changed since the last commit?").

### One-time setup

1. Install `uv` (needed to run the git MCP server too, if you use it):

powershell -c "Set-ExecutionPolicy RemoteSigned -scope CurrentUser"
irm https://astral.sh/uv/install.ps1 | iex

   Open a **new** terminal afterward so the PATH change takes effect.

2. Install Python 3.10+ if you don't have it (https://python.org).

3. Install the MCP Python SDK, **pinned below version 2** (this project's
   `server.py` uses the v1 API):

pip install "mcp<2"


4. Clone this repo and build it in **Release** mode:
   - Visual Studio → **Build → Configuration Manager** → set
     Configuration to **Release** → **Build → Build Solution**
   - `server.py` (included in this repo) automatically finds the
     compiled `.exe` next to it, so no path editing is needed inside
     this repo.

### Wire it into a PLC project's repo

In the PLC project's own Git repo (the one created from
`plc-version-control-template`), create or edit `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "git": {
      "command": "uvx",
      "args": ["mcp-server-git", "--repository", "."]
    },
    "plc-export": {
      "command": "python",
      "args": ["FULL_PATH_TO_THIS_REPO\\server.py"]
    }
  }
}
```

Replace `FULL_PATH_TO_THIS_REPO` with wherever you cloned **this** tool
repository on your machine, for example:

C:\Users\YourName\source\repos\PLC_Openness_Export


Open the PLC project's folder in Cursor, and try asking it:

Export the PLC project at [path to .ap16] to [path to this project's repo folder]


Cursor will call the tool, show you what changed, and can commit and
push for you if you confirm — no manual `git` commands or running the
`.exe` by hand required.

## Troubleshooting

| Problem | Likely fix |
|---|---|
| "Permission Error" message on run | Your account isn't in the "Siemens TIA Openness" group — see setup step 3 above |
| "That file wasn't found" when entering project path | Double-check the path; make sure you're pointing at the `.ap16` file itself, not the folder |
| "Another project is already open" error | Close whatever project is currently open in TIA Portal, then run the tool again |
| Tool won't launch at all | Check that .NET Framework 4.7.2+ is installed |
| TIA Portal shows a "Git Commit" popup | Click Cancel — see the note above; this project doesn't use VCI's built-in Git feature |
| MCP export call times out | TIA Portal can be slow to cold-start; the MCP wrapper's timeout is set to 600 seconds, but very large projects may need it increased further in `server.py` |
| Export leaves TIA Portal instances running after a timeout | Close them manually via Task Manager before retrying, to avoid "another project is already open" errors |

## Related repositories

- New PLC projects should be set up using:
  `plc-version-control-template`