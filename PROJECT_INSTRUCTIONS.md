FocusApp project: how to work in any chat

Focus is a Windows WPF app (C#, .NET 10). The source of truth is the GitHub repository
https://github.com/OWNER/FocusApp (replace OWNER with my GitHub name). The build manager is
https://claude.ai/artifact/QCxf3Z5q5wgYn9kRPyvnjX

When I say "go" (or ask for any FocusApp change):
1. Get the latest code: git clone https://github.com/OWNER/FocusApp into /home/claude/FocusApp.
   If that fails, use the newest FocusApp zip in this chat or in project knowledge, and ask me
   to upload one if there's none.
2. Read MAINTENANCE.md and CHANGELOG.md first; follow the file map and code style in MAINTENANCE.md.
3. Read the build manager with the Artifact tool (action "read_db", collection "builds", and
   collection "ideas"). The target is the build with status "building"; if there is none, the
   lowest "planned" version. Its unchecked items, plus anything I ask for in the chat, are the work.
4. Make the changes. Raise <Version> in FocusApp.csproj to the target version and add a
   "## x.y.z" section to CHANGELOG.md. Update MAINTENANCE.md's file table for new files.
   Never change the AppId in FocusSetup.iss or the UpdateRepo wiring in FocusApp.csproj.
5. Update the build manager with write_db: tick finished items (done: true), add items for
   extra changes I asked for, and set status to "building". I mark it released myself.
6. Give me a zip of the whole project (no bin, obj or installer folders) and list the files
   that changed, so I can upload them to GitHub. GitHub then builds FocusSetup.exe and installed
   copies of Focus offer the update.
7. When I say I've uploaded a version, check it with
   curl -s https://api.github.com/repos/OWNER/FocusApp/releases/tags/vX.Y.Z
   If FocusSetup.exe is listed in its assets, set that build's status to "released" and its
   date to today in the build manager, so its download link appears there. If not, check
   https://api.github.com/repos/OWNER/FocusApp/actions/runs?per_page=1 and tell me whether the
   GitHub build is still running or failed.
