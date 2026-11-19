# Listful for Windows

An outliner for task lists, in the spirit of Workflowy but with no subscription. Lists are plain
Markdown files you own. This is the Windows version of [Listful](https://github.com/drchrispinnock/listful)
for the Mac, iPhone and iPad, and reads and writes the same files.

- Bulleted tasks you can nest with Tab / Shift+Tab and collapse to their parent
- Tasks are Open, Done (ticked and struck through) or Decrufted (tidied away, never deleted)
- `#`, `##`, `###` headings; a `#` on the first line is the list's title
- Repeating checklists: a template copied into a fresh list each day, week, month…
- A sidebar of lists: one folder of `.md` files, its subfolders, and any Markdown files or
  folders elsewhere that you add

## Sharing lists between computers

Listful keeps your lists in one folder. Put it in a folder that OneDrive or Dropbox syncs (Listful
offers these the first time it runs) and choose the same folder in Listful on your other
computers, including Listful on a Mac (Settings › Task lists › Choose…). When a list is changed
in two places at once, the changes are merged line by line; if they clash, your version is kept
beside the list as a conflict copy.

## Building

Requires Windows 10 (19041) or later and the .NET 10 SDK.

```powershell
git submodule update --init      # the Mac app's sources, in upstream/
dotnet build Listful.slnx -p:Platform=x64
dotnet test --project tests/Listful.Core.Tests
```

The Mac app's sources are the submodule `upstream/`.
