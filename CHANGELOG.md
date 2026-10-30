# Focus changelog

Each version gets a "## x.y.z" heading. The GitHub build uses the section that matches
<Version> in FocusApp.csproj as the release notes, and Focus shows them when it offers the update.

## 1.4.0

- Block lists can now include Freedom web blocking: each list has a Freedom web blocking section with
  your Freedom blocklists (exactly as Freedom has them; Sync gets them again). When a focus session that
  uses the list starts, Freedom turns those blocklists on for exactly that session's length. With a whole
  day planned, Freedom starts again at each focus session after a break.
- Block lists page redesigned: list cards with what each list holds, a delete button on every card, and
  the editor in two clear sections (Apps, Freedom web blocking). Freedom's connection and devices are at
  the bottom left (Freedom…).
- Deleting a block list now works while a day is running; only the list blocking the current session
  waits until that session ends.
- Freedom window: devices are listed first; the old "start with every focus session" option now reads
  "Also start the blocklists ticked below with every focus session".

## 1.3.0

- Freedom: connect your Freedom account in Settings, Focus sessions, Open Freedom (or Freedom… in the
  tray menu). Focus shows your Freedom blocklists and devices exactly as Freedom has them: lists you add
  or delete in Freedom appear or disappear in Focus each time it syncs (when the window opens, every
  30 minutes, and before each session). Tick the ones to use, and turn on Start Freedom with every focus
  session; Freedom then runs for the length of each focus session. You can also start a Freedom session
  from the window, sync, or open the Freedom dashboard.
- Practice timelines: Settings, Greek practice, Practice timelines. Each practice (Words, Alphabet,
  Concepts) has its rounds on a timeline with today's meetings and lunch shaded. Drag a round to move it
  (snaps to 15 minutes), double-click to add one, right-click to remove one. Use for today, or Save as
  default for every day. A round that lands in a meeting still waits until the meeting ends.
- Practice timelines: an All practice row shows every practice's rounds together, so you can spread
  them apart or put them back to back.

## 1.2.1

- Dashboard: the numeral badge and FOCUS SESSION sit centered over the ring timer, with more room
  between them and the ring.
- Floating timer: a long personal project name in the Personal tab is trimmed to fit, so the caret
  beside it is always there to change or clear your choice.

## 1.2.0

- Tasks: Today and Tomorrow buttons on each task (they appear when the cursor is over it; a task already
  set for one day shows only the other). They replace the ☀ button.
- Tasks: Today is now a tab in the top bar, between Undated and Tomorrow, instead of in the Views list.
- Floating timer: focus sessions show their number as a Roman numeral in a small serif ring, then the
  timer, then FOCUS SESSION in spaced capitals. A session you named shows its name instead. The dashboard
  uses the same look above the ring timer.
- Floating timer: a caret beside Personal chooses which personal tasks show: Today, This week, All, or
  one project. The tab shows your choice and remembers it.
- Greek review: an even two-column grid with questions numbered 1, 2, 3 and answers lettered A, B, C,
  the same text size on both sides, each matched pair labelled on both sides, and section numbers at
  the start of a question shown as small tags. The review uses nearly the whole screen.
- Date fields (due date, snooze, and Another day in the end-of-day review): clicking opens a small
  calendar. Typing "friday" or "10/14" still works.
- End-of-day review: a Resolve option for tasks you finished but didn't tick off.
- Scripture references (like John 3:16 or Rom 8:28-30) in task names, the floating timer, the end-of-day
  review and your concepts open in Logos Bible Software. The task editor lists the references in a
  task and its notes as links.

## 1.1.0

- Focus now updates itself from GitHub Releases: Check for updates in the tray menu, and a quiet
  check twice a day (Settings, Features, App, Check for updates).
- The installer is built automatically on GitHub; no Windows build tools needed.

## 1.0.0

- First installer release.
