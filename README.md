\# PLC Hardware Export Tool



Exports the hardware configuration (CPU, modules, network settings) from a

TIA Portal project into a `hardware\_config.json` file, so it can be tracked

in Git alongside the program block exports from TIA Portal's Version Control

Interface (VCI).



\## One-time setup (per computer)



Before this tool will run, four things need to be true on your machine:



1\. \*\*TIA Portal Openness must be installed.\*\*

&#x20;  This normally comes bundled with TIA Portal itself. To check, look for:

&#x20;  ```

&#x20;  C:\\Program Files\\Siemens\\Automation\\Portal V16\\PublicAPI\\V16\\Siemens.Engineering.dll

&#x20;  ```

&#x20;  If that file exists, you're set.



2\. \*\*.NET Framework 4.7.2 or later must be installed.\*\*

&#x20;  Most Windows machines already have this. If the tool fails to launch at

&#x20;  all, this is the first thing to check.



3\. \*\*Your Windows account must be a member of the local

&#x20;  "Siemens TIA Openness" security group.\*\*

&#x20;  This is the step almost everyone gets stuck on — it requires admin

&#x20;  rights to set up. If you don't see this group, or don't have admin

&#x20;  rights, ask IT to do this for you.



&#x20;  \*\*To add yourself (requires an Administrator Command Prompt):\*\*

&#x20;  ```

&#x20;  net localgroup "Siemens TIA Openness" "YOUR\_WINDOWS\_USERNAME" /add

&#x20;  ```

&#x20;  Not sure of your exact username? Run `whoami` first — it prints your

&#x20;  account name in `COMPUTERNAME\\username` format.



&#x20;  \*\*After running this command, restart your computer.\*\* The group

&#x20;  membership won't take effect until you do.



4\. \*\*Git must be installed\*\*, with your name and email configured so

&#x20;  commits are attributed to you correctly:

&#x20;  ```

&#x20;  git config --global user.name "Your Name"

&#x20;  git config --global user.email "your.email@company.com"

&#x20;  ```

&#x20;  Without this, Git may use a generic or missing identity, which makes

&#x20;  it hard to tell who made which change later.



If you skip step 3, the tool will still run but will fail with a

permission error the moment it tries to talk to TIA Portal. The tool

will tell you this directly and print the exact command above if that

happens.



\## A quirk to know about: TIA Portal's own "Git Commit" popup



When you export a block through TIA Portal's Version Control Interface

(VCI), TIA Portal may show its own \*\*"Git Commit"\*\* dialog asking for a

commit message. \*\*Click Cancel on this dialog.\*\*



This tool's workflow does not use VCI's built-in Git commit feature — it

uses one unified Git repository (specific to each PLC project) that you

manage manually with the commands below. If you click OK on VCI's dialog,

it may show an error, or create a second, disconnected version history

that doesn't match the rest of the project. Just Cancel that dialog and

use `git add` / `git commit` yourself, as shown below.



\## How to run it



1\. Make sure your TIA Portal project is \*\*closed\*\* (or open in the same

&#x20;  TIA Portal instance you intend to use — don't have two different

&#x20;  projects open when you run this).

2\. Run `PLC\_Openness\_Export.exe` (or `dotnet run` / F5 if running from

&#x20;  source).

3\. If you've used the tool before, it will show you the last project path

&#x20;  and export folder you used — press Enter to reuse them, or type a new

&#x20;  path to use a different project.

4\. If this is your first time, you'll be asked to enter:

&#x20;  - The full path to your project's `.ap16` file

&#x20;  - The folder to export into (this should be your PLC project's own

&#x20;    Git-tracked folder — see the `plc-project-template` repo for setting

&#x20;    that up)

5\. Wait for TIA Portal to open/attach and the project to load.

6\. When you see `Success. Press Enter to exit.`, the export is done.



\## What to do with the exported file



This file is meant to be committed to Git alongside the VCI block

exports, in that project's own repository:



```

git add .

git commit -m "Describe what changed, e.g. 'Added digital input module'"

git push

```



Re-run this tool any time hardware configuration changes, then repeat

the commit step. `git diff` will show exactly what changed between

versions.

## Optional: Use this tool from Cursor (AI-assisted, no manual commands)

Instead of running the .exe directly, you can wire this tool into Cursor
so you can trigger exports by just asking in plain English (e.g. "export
this PLC project").

### One-time setup

1. Install `uv` (needed to also run the git MCP server, if you're using
   that too):

powershell -c "Set-ExecutionPolicy RemoteSigned -scope CurrentUser"
irm https://astral.sh/uv/install.ps1 | iex

2. Install Python 3.10+ if you don't have it (https://python.org).
3. Install the MCP Python SDK (pinned below v2, since this project uses
   the v1 API):

pip install "mcp<2"

4. Build this project in **Release** mode (Build → Configuration Manager
   → Release → Build Solution), so `server.py` can find the compiled
   `.exe` next to it.

### Wire it into a project

In the PLC project's own repo (the one you're tracking with Git), create
a file at `.cursor/mcp.json` with:

```json
{
  "mcpServers": {
    "git": {
      "command": "uvx",
      "args": ["mcp-server-git", "--repository", "."]
    },
    "plc-export": {
      "command": "python",
      "args": ["[full path to this tool repo]\\server.py"]
    }
  }
}
```

Replace `[full path to this tool repo]` with wherever you cloned this
repository on your machine (e.g.
`C:\Users\YourName\source\repos\PLC_Openness_Export`).

Open that PLC project's folder in Cursor, and ask it something like:

Export the PLC project at [path to .ap16] to [path to this repo folder]



\## Troubleshooting



| Problem | Likely fix |

|---|---|

| "Permission Error" message on run | Your account isn't in the "Siemens TIA Openness" group — see setup step 3 above |

| "That file wasn't found" when entering project path | Double-check the path; make sure you're pointing at the `.ap16` file itself, not the folder |

| "Another project is already open" error | Close whatever project is currently open in TIA Portal, then run the tool again |

| Tool won't launch at all | Check that .NET Framework 4.7.2+ is installed |

| TIA Portal shows a "Git Commit" popup after exporting a block | Click Cancel — see the note above |



\## Related repositories



\- Project-specific version control repos are created from:

&#x20; `plc-project-template` 
