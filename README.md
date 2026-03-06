# ra-logix-designer-vcs-custom-tools

Git version control integration for Rockwell Automation Studio 5000 Logix Designer. This toolset adds custom buttons directly inside Logix Designer for committing, diffing, restoring, and managing pull requests — all without leaving the IDE.

## Quick Start

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Studio 5000 Logix Designer with the Logix Designer SDK
- [GitHub CLI (`gh`)](https://cli.github.com/) — for Pull Request functionality
- [VS Code](https://code.visualstudio.com/) — for Difftool (configure as git difftool)

### Build & Install

```sh
git clone https://github.com/Brok3d3ad/ra-logix-designer-vcs-custom-tools.git
cd ra-logix-designer-vcs-custom-tools
dotnet build -c Release
```

Copy the custom tools menu into Logix Designer:

```sh
copy "artifacts\bin\Release\Assets\CustomToolsMenu.xml" "C:\Program Files (x86)\Rockwell Software\RSLogix 5000\Common\CustomToolsMenu.xml"
```

Restart Logix Designer. The new buttons will appear in the custom tools toolbar/menu.

### Setup for a Project

1. Place your `.ACD` file inside a Git repository
2. Run **Commit** from Logix Designer — on first run it will create a `<ProjectName>_L5xGit.yml` config file next to your ACD
3. The config file stores:
   ```yaml
   destination_path: C:\path\to\repo\ProjectName
   prompt_for_commit_message: true
   ```

That's it. All buttons work from this point.

## Custom Tool Buttons in Studio 5000

### Commit

Exports the currently open ACD project to Git.

**What it does:**
1. Prompts for a commit message
2. Copies ACD to a temp location
3. Converts ACD to L5X
4. Explodes L5X into version-control-friendly XML files
5. Stages and commits all changes to Git

### Difftool

Opens a side-by-side colored diff in VS Code showing what changed in the last commit.

**What it does:**
1. Finds all files changed between the last two commits
2. Opens each changed file in VS Code's native diff view (split pane, red/green highlighting)

**VS Code setup** (one-time):
```sh
git config --global diff.tool vscode
git config --global difftool.vscode.cmd "code --wait --diff $LOCAL $REMOTE"
git config --global diffEditor.wordWrap on
```

### Restore / RestoreCurrentFile

Rebuilds your ACD file from the Git history. The inverse of Commit.

**What it does:**
1. Implodes the exploded XML files back into an L5X
2. Converts L5X to ACD
3. If the project is open in Logix Designer — **automatically closes it**
4. Backs up the existing ACD (`.BAKnnn.ACD`)
5. Replaces the ACD with the restored version
6. **Reopens the project** in Logix Designer

Two variants:
- **RestoreCurrentFile** — restores the currently open project
- **Restore** — prompts you to choose which ACD file to restore

### Pull

Pulls changes from the remote repository and recompiles all affected ACD files.

**What it does:**
1. Discovers all projects in the repo (via `*_L5xGit.yml` config files)
2. Shows which projects are currently open in Logix Designer
3. **Warns that local unsaved changes will be lost** — asks for confirmation
4. Closes all affected Logix Designer instances
5. Runs `git pull`
6. Detects which projects had changes
7. Recompiles only the changed projects (implode + convert to ACD)
8. Reopens projects that were open before the pull

### PullRequest

Creates a GitHub pull request from your unpushed commits.

**What it does:**
1. Shows all unpushed commits
2. Prompts for a **branch name**
3. Prompts for a **PR title**
4. Prompts for a **PR description**
5. Creates a new branch
6. Pushes it to origin
7. Creates a pull request on GitHub (via `gh` CLI)
8. Switches back to the original branch

**Requires:** GitHub CLI installed and authenticated (`gh auth login`)

## Folder Structure

A typical project setup looks like this:

```
my-repo/                          <- git init here
├── .git/
├── .gitignore
├── MyProject.ACD                 <- your Logix Designer project (gitignored)
├── MyProject_L5xGit.yml          <- config file (auto-created on first commit)
└── MyProject/                    <- exploded content (version controlled)
    └── RSLogix5000Content/
        ├── RSLogix5000Content.xml
        ├── export-options.yaml
        ├── Programs/
        │   └── MainProgram.xml
        └── ...
```

Recommended `.gitignore`:
```
*.ACD
*.Wrk
*.Sem
```

## CLI Usage

The tools can also be used from the command line:

```sh
l5xgit commit --acd path/to/project.ACD
l5xgit difftool --acd path/to/project.ACD
l5xgit restoreacd --acd path/to/project.ACD
l5xgit pull --acd path/to/project.ACD
l5xgit pullrequest --acd path/to/project.ACD
l5xgit explode --l5x project.L5X --dir output/
l5xgit implode --dir output/ --l5x project.L5X
l5xgit acd2l5x --acd project.ACD --l5x project.L5X
l5xgit l5x2acd --l5x project.L5X --acd project.ACD
```

## Project Structure

- `L5xploderLib/` — Core library for exploding/imploding L5X files
- `L5xGitLib/` — Git integration and configuration
- `L5xCommands/` — Command implementations
- `l5xgit/` — CLI executable (all commands)
- `l5xplode/` — CLI executable (explode/implode only)

## Limitations

- Source-protected content is non-human readable in XML; encoded content mutates with each export causing false diffs
- Projects that don't verify/export cleanly cannot be converted between ACD and L5X
- Output formats may change in future versions

## License

MIT License — see [LICENSE](LICENSE).
