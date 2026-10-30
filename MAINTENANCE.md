# Focus: maintenance guide

This guide is for keeping Focus working, or making small changes, without help.
Everything runs on your PC. Focus has no AI and sends nothing anywhere.

## Build and install

You need the free .NET SDK (version 10 or newer) from https://dotnet.microsoft.com/download.

Open PowerShell and run these one line at a time:

    cd C:\Projects\FocusApp
    dotnet run

That runs a test copy. To make and install the real app:

1. Quit Focus (right-click its tray icon by the clock, then Quit Focus).
2. Run:

       dotnet publish -c Release
       Copy-Item "bin\Release\net10.0-windows\win-x64\publish\FocusApp.exe" "$env:LOCALAPPDATA\Programs\Focus\" -Force

3. Open Focus from the Start menu.

The finished FocusApp.exe contains everything it needs, including its own copy of .NET.
It keeps working even if .NET or Windows updates.

## Releasing a new version (no Windows build needed)

Focus is built on GitHub, not on your PC. The file `.github/workflows/release.yml` tells GitHub to
build FocusApp.exe and FocusSetup.exe on its own Windows computers and publish them as a Release.

1. Raise `<Version>` in FocusApp.csproj (for example 1.1.0 to 1.2.0).
2. Add a `## 1.2.0` section to CHANGELOG.md saying what changed (it becomes the release notes).
3. Upload the changed files to the GitHub repository (Add file, Upload files, Commit changes).
4. In a few minutes, the Actions tab shows a green tick and Releases has Focus 1.2.0 with FocusSetup.exe.

Uploading without raising the version builds nothing new. To install on a new PC, download
FocusSetup.exe from the Releases page. Installed copies offer the update themselves: a notice
from the tray icon, or right-click the tray icon, Check for updates. Updating closes Focus for a
few seconds, reopens it, and keeps all data. Focus never updates during a session or break, and
never without asking.

The repository must be public, because Focus downloads updates without signing in.
Copies built on your own PC (dotnet publish) don't check for updates at all.

## Making an installer on your PC (FocusSetup.exe)

This makes a normal Windows installer, like apps you download from the web: a setup wizard,
a Start menu entry, an optional desktop shortcut, and an entry in Windows Settings > Apps
for uninstalling.

1. Install Inno Setup 6 (free): https://jrsoftware.org/isdl.php
2. In PowerShell, in the project folder, run:

       powershell -ExecutionPolicy Bypass -File .\build-installer.ps1

3. The installer appears in the `installer` folder as FocusSetup.exe. Share it any way you
   like (OneDrive, Google Drive, a USB stick, a website).

For a new version, raise the `<Version>` number in FocusApp.csproj (for example 1.0.1),
rebuild the installer, and run it on each PC. It updates Focus in place and keeps all data.

Windows shows "Windows protected your PC" for installers that aren't signed with a paid
code-signing certificate. Click More info, then Run anyway. Only a purchased certificate
removes that screen.

## Installing on another PC (without the installer)

1. Build the final app (`dotnet publish -c Release`, above).
2. Copy `bin\Release\net10.0-windows\win-x64\publish\FocusApp.exe` to the other PC
   (USB stick, OneDrive, or a shared folder; it's too large for most email).
3. On that PC, open FocusApp.exe. If Windows shows "Windows protected your PC", click
   More info, then Run anyway. (That screen appears for any program that isn't signed with
   a paid certificate.)
4. Choose Yes to install. Focus is added to the Start menu and starts at sign-in. No
   administrator rights are needed, and nothing else needs installing.

Each Windows user has their own plans, settings and calendar links. To update Focus later,
open a newer FocusApp.exe the same way; it replaces the installed copy and keeps all data.
To remove it: Settings, Uninstall Focus.

## Where your data lives

    %AppData%\FocusApp

(Paste that into the File Explorer address bar.) To back up, copy the whole folder.

| File                | What it holds                                             |
|---------------------|-----------------------------------------------------------|
| settings.json       | Your settings                                             |
| layouts.json        | Saved layouts                                             |
| blocklists.json     | Block lists                                               |
| plans\             | One file per planned date (for example 2026-10-09.json)   |
| calendars.json      | Your calendar connections (links are encrypted with your Windows account) |
| calendar-cache.json | The last copy of your calendar events, so plans work offline |
| run-state.json      | The session that's running (lets Focus resume after a restart) |
| focus-stats.json    | Focus minutes per day                                     |
| task-bin.json       | Unfinished tasks waiting in the task bin                  |
| resolved-tasks.json | Every task you've finished, with the date                 |
| task-removed.json   | Tasks finished or deleted in the last 90 days (personal-removed.json for Personal), so no old copy brings them back |
| personal-*.json     | Personal tasks, finished personal tasks, personal projects (synced) |
| concepts.json       | Your concepts and how well you know each one (synced)     |
| alphabet.json       | Your progress on the alphabet (synced)                    |
| vocab.json          | Your Greek words and how well you know each one (synced)  |
| projects.json       | Projects, their colors, shortcuts and sections            |
| calendar-share.json | The next three weeks of meetings, for the phone app       |
| Daily records\\      | One Markdown file per day (unless an Obsidian folder is chosen) |
| templates.json      | Task templates                                            |
| freedom.json        | Your Freedom connection and choices, this PC only (the sign-in is encrypted with your Windows account; your password is never saved) |
| log.txt             | Error log. Look here first if something goes wrong        |

All files are plain text (JSON). If one gets damaged, Focus keeps a copy named
`...unreadable-<date>` and starts that part fresh.

## Sync between computers

Settings, Sync between computers, Sync with OneDrive. Focus then keeps these in
`OneDrive\Focus` instead of `%AppData%\FocusApp`: plans, layouts.json, blocklists.json,
task-bin.json, resolved-tasks.json, and focus-stats-PCNAME.json (one per computer, added together). Everything else
(settings, calendar links, the running session, the log) stays on each computer.
Which folder is used is stored in sync.json in `%AppData%\FocusApp`.

If the same thing is changed on both computers at nearly the same moment, the last save wins,
and OneDrive may keep a duplicate named with a computer name. Those duplicates can be deleted.

## Changing common things

Most numbers you might want to change are in **Defaults.cs**, with a comment on each:
the 10-second warning, the 3- and 8-minute breaks, the break phrase, the calendar hours (6 AM to 10 PM),
the Devotions, morning break and lunch times, the break after meetings, the allowed focus and break
lengths for automatic plans, and the nudge times and messages.

Change a value, save the file, and rebuild (see above).

Colors are in **Themes\Light.xaml** and **Themes\Dark.xaml**. Each line is a named color
like `FocusBrush` (focus sessions), `BreakBrush` (breaks), or `MeetingBrush` (meetings).

## What each file does

| File                     | Purpose                                                        |
|--------------------------|----------------------------------------------------------------|
| App.xaml, App.xaml.cs    | Startup, one-copy-at-a-time, error handling, the helper process for extreme lock |
| MainWindow.xaml          | The main window's layout                                       |
| MainWindow.xaml.cs       | Main window: tray icon, quitting, running sessions, layouts    |
| MainWindow.Plan.cs       | Day plan timeline, editing blocks, copy and paste              |
| MainWindow.Picker.cs     | The "block apps" and "block list" pickers                      |
| MainWindow.Lists.cs      | The Block lists tab: apps and Freedom web blocking in each list, deleting lists |
| MainWindow.Session.cs    | Pomodoro, quick focus, short breaks, settings, nudges, focus time |
| AutoPlanner.cs           | Fills a day around meetings, the break and lunch (plain arithmetic, no AI) |
| PlanReflow.cs            | Keeps fixed times in place while you drag, resize or delete blocks |
| MainWindow.Days.cs       | Plans for each date, auto-start, the Calendar tab, calendar settings |
| CalendarService.cs       | Downloads your calendar links (https only) and keeps an offline copy |
| IcsParser.cs             | Reads calendar (.ics) files, including repeating meetings      |
| WeekTimeline.cs          | The week calendar drawing                                      |
| MainWindow.Dashboard.cs  | The dashboard: today's calendar, ring timer, focus totals       |
| RingTimer.cs             | The circular countdown on the dashboard                        |
| MainWindow.Tasks.cs      | Tasks: the bin, filters, today's plan, details, quick-add key  |
| QuickAddWindow.xaml(.cs) | The small "add a task" box opened by Ctrl+Shift+Space          |
| MainWindow.TaskPower.cs  | Repeats, reminders, steps, Upcoming, board, search, undo, review, feature switches |
| NaturalLanguage.cs       | Reads "tomorrow at 3pm", "every friday" and similar           |
| ReviewWindow.xaml(.cs)   | The end-of-day review                                          |
| Features.cs              | The list of features you can switch off in Settings           |
| Guide.cs                 | The "How Focus works" guide in Settings (edit text here)       |
| MainWindow.Home.cs       | The Home page, the Day plan's edit popup, Ctrl+Enter and Esc on popups |
| MainWindow.Update3.cs    | Command bar, start-focus key, website blocking, projects, templates, goals, stats, workload, automations |
| CommandBarWindow.xaml(.cs) | The Ctrl+K command bar                                      |
| MainWindow.Update4.cs    | Extending sessions, new meetings, filling part of the day, leftover time, tasks with a time, website blocking, the floating timer's tasks, daily records, calendar sharing, themes, weekends |
| MainWindow.Update5.cs    | Selecting and dragging tasks, renaming in place, right-click menus, projects and categories editors, export and print, keyboard shortcuts (HandleShortcut), redo, welcome tour |
| MainWindow.Sites.cs      | The chip-style website editor (Settings, block lists, sessions) |
| Vocab.cs                 | Greek practice: the review bank, levels and spaced repetition, search |
| GreekWords.tsv           | Every Greek NT word: Greek, tab, meaning, tab, NT count (most frequent first). Meanings: Dodson's lexicon (CC0), word for word (only "a"/"an" and final periods removed), with his fuller definition in column 4; counts: SBLGNT via MorphGNT (CC BY-SA 3.0); column 5 is the earlier wording, used once to update saved words. |
| VocabWindow.xaml(.cs)    | The matching round that pops up |
| VocabManagerWindow.xaml(.cs) | Finding, adding and editing Greek words |
| Alphabet.cs              | Alphabet practice: modern Greek sounds for every letter and pair (edit them here) |
| MainWindow.Settings.cs   | Settings pages, search, automatic saving, Reset this page |
| TaskLook.cs              | How tasks look: Default, Compact or Paper (also used when printing) |
| Personal.cs              | Personal tasks: the Personal set (see TaskStores in Models.cs), for Pomodoro, the floating timer and quick add |
| MainWindow.Personal.cs   | Opening the Tasks page on the Personal set, its look, the quick-add shortcut, and Personal in Pomodoro |
| Concepts.cs              | Concepts practice: your own questions and answers |
| ConceptsWindow.xaml(.cs) | Entering and editing concepts (and pasting a list) |
| MainWindow.Practice.cs   | Concepts schedule, practice outside sessions, extra answers |
| MainWindow.Vocab.cs      | When Greek practice pops up (never in meetings or lunch) |
| SyncMerge.cs             | Three-way merging for synced lists, so stale computers never undo newer changes |
| TitleBar.cs              | Makes window title bars match the theme |
| AutoScroll.cs            | Middle-button scrolling in every window |
| SiteRules.cs             | The website categories (edit the lists here) and how addresses are matched |
| BlockedToast.xaml(.cs)   | Small notices, including the ones with an Undo button |
| DailyRecords.cs          | The daily Markdown records (Obsidian-ready; the My notes section is never overwritten) |
| Themes\*.xaml            | Colors for Classic (Light/Dark), Monochrome and Calm |
| Progress.cs              | Points, levels, streaks, records and badges for Stats (tune the numbers here) |
| HostsBlocker.cs          | Website blocking through the Windows hosts file                |
| logo.png, app.ico        | The logo and app icon                                          |
| Sync.cs, MainWindow.Sync.cs | Syncing through a OneDrive folder                           |
| Installer.cs             | Installing, updating and uninstalling for the current user     |
| SessionLook.cs           | The numeral badge: Roman numerals and spaced capitals for focus sessions (floating timer, dashboard) |
| Fonts\*.ttf             | Cormorant Garamond SemiBold for the numeral badge (SIL Open Font License, OFL.txt) |
| DatePopup.cs             | The small pop-up calendar on date fields                       |
| FreedomService.cs        | Freedom (freedom.to): signing in, reading your blocklists and devices, starting a session |
| MainWindow.Freedom.cs    | The Freedom window, and starting Freedom for each focus session whose block lists include Freedom blocklists |
| MainWindow.Timelines.cs  | Practice timelines: your own round times for Words, Alphabet and Concepts |
| ScriptureLinks.cs        | Finds Scripture references and opens them in Logos (book names and abbreviations are listed here) |
| Updater.cs               | Checks GitHub Releases and downloads FocusSetup.exe            |
| MainWindow.Updates.cs    | Check for updates (tray menu), the twice-a-day check, installing an update |
| .github\workflows\release.yml | GitHub builds the app and installer and publishes a Release |
| CHANGELOG.md             | What changed in each version (used as the release notes)      |
| FocusSetup.iss           | The installer recipe (for Inno Setup)                          |
| build-installer.ps1      | Builds the app and the installer in one go                     |
| SessionRunner.cs         | The timer that moves through sessions and breaks               |
| AppBlocker.cs            | Closes blocked apps                                            |
| AppCatalog.cs            | Finds the apps installed on the PC                             |
| DayTimeline.cs           | The day calendar drawing                                       |
| NumberBox.cs             | Number fields with up/down arrows                              |
| Models.cs                | The data that gets saved                                       |
| Defaults.cs              | Numbers and words that are easy to change                      |
| Storage.cs, Log.cs       | Saving files and the error log                                 |

## Safety

- No AI, no accounts, no ads, no tracking. Updates are offered, never installed without asking.
- Internet use: downloading the calendar links you add, and checking your GitHub Releases page
  for updates (Updater.cs; switch off in Settings, Features), over secure https only. An update is
  only taken from github.com, and is checked against GitHub's SHA-256 fingerprint before it runs.
  If you connect Freedom, Focus also talks to freedom.to (FreedomService.cs): it signs in, reads your
  blocklists and devices, and starts the Freedom sessions you ask for. Freedom has no official public
  API; Focus uses the service Freedom's own apps use, so if Freedom changes it, the Freedom window shows
  an error and Focus's own blocking carries on. Test connection writes Freedom's replies to log.txt.
- Calendar links: links to plain http or to raw computer addresses are refused. Calendar
  downloads are size-limited and only read as calendar text; nothing from them is ever run.
- Calendar links are encrypted with your Windows account (Windows DPAPI).
- Focus installs only for your user, needs no administrator rights, and changes nothing in
  Windows except its own Start menu shortcut and its "start at sign-in" entry.
- The only programs Focus starts are itself (the extreme-lock helper and the installed copy),
  File Explorer (Open data folder), a hidden cleanup command when you uninstall, and a downloaded
  FocusSetup.exe when you accept an update.
- It closes only the apps you choose to block, and never parts of Windows.

## Internet use

Focus connects to the internet for three things: downloading the calendar links you add in
Settings, checking your GitHub Releases page for a newer version, and (only if you connect it)
your Freedom account at freedom.to. All use secure https. Apart from your Freedom sign-in and the
Freedom sessions you start, it never sends anything anywhere, never changes your calendar, and has
no AI, ads or tracking.

If a calendar stops updating (Settings shows "Couldn't update"), the link may have been
turned off. Publish the calendar again, remove the old one in Settings, and add the new link.

## Website blocking

permission prompts. The older hosts-file method below is only used to clean up after older versions.

When switched on (Settings, Features), Focus adds a marked section to
`C:\Windows\System32\drivers\etc\hosts` while a day or session runs, and removes it afterwards.
Windows asks for permission each time. If anything ever goes wrong, open that file in Notepad
as administrator and delete the lines between "# >>> Focus website blocking" and
"# <<< Focus website blocking". Microsoft Defender sometimes flags or undoes hosts-file changes
for well-known sites; if that happens, add an exclusion for the hosts file or switch this feature off.

## Switching features off

Settings, Features lists the features from the last two task updates, each with its own switch.
Switching one off hides it and stops its behavior; your data stays, so switching it back on
picks up where you left off. To add a new switch, add a line to Features.cs and check it with
`_settings.On("key")` where the feature does its work.

## If something goes wrong

- **Build shows red errors:** the message names the file and line. Undo your last change there.
- **Focus acts oddly:** check log.txt in the data folder.
- **Start over completely:** quit Focus, rename the `%AppData%\FocusApp` folder, and open Focus.
  It starts fresh; your old data is still in the renamed folder.
- **An app won't block:** open the app picker, search for it, or use Find app file. Apps
  running as administrator can't be closed by Focus.

## Years from now

Focus targets .NET 10. When a newer long-term version of .NET comes out and you want to move to
it, open FocusApp.csproj, change `net10.0-windows` to the new version (for example
`net12.0-windows`), and rebuild. Also update the folder name in the Copy-Item line above.
You don't have to do this: the published FocusApp.exe keeps working on its own.
