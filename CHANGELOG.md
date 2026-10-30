# Focus changelog

Each version gets a "## x.y.z" heading. The GitHub build uses the section that matches
<Version> in FocusApp.csproj as the release notes, and Focus shows them when it offers the update.

## 1.7.1

- One schedule for practice: Words, Alphabet and Concepts each come up at the times on their own row of Practice
  timelines, whether or not a focus session is running. Focus only needs to be open (the tray is enough). The
  "Also practise outside focus sessions, every … minutes" option is gone (that's what brought rounds up at seemingly
  random times), and so are "Rounds a day (in sessions)" and the practice hours: your current times were moved onto
  the timelines once, so nothing shifts.
- Practice, lessons, the card review and All-books practice now all follow the same rule: they wait out meetings,
  breaks and lunch, and no longer wait for a focus session to end.
- Pause all practice on weekends (Greek practice settings, Training settings or the timelines): nothing pops up by
  itself on Saturdays and Sundays and no lesson is required, but you can still start anything yourself. Shared with
  your other computers.

## 1.7.0

- Training, reading: choose the font (any font on your PC), size and background (Focus theme, Paper, Sepia, White,
  Sage, Gray, Slate or Night) under Aa while reading, or in Training settings. The reading fills the window with its
  own scroll bar, and the mouse wheel scrolls it.
- Page view turns like a Kindle: click or tap the left or right side of the page (or use the arrow keys or the
  mouse wheel).
- Highlights and notes: select text and a small pop-up appears beside it with the colors and Note. Select a
  highlight to change its color, edit its note or remove it.
- Show where this is in the text: the reading opens at the answer, and the sentence it comes from flashes a few times.
- Memory cards grade like the BMA app: ✕ (comes back later in this lesson), ✓✓ (know it well: away for a long time)
  and ✓ (know it fairly well: back after a while). Keys 1, 2, 3 grade and Space flips.
- Evening card review: a five-minute pop-up with the hardest cards from that day's lesson. Move it with its slider on
  the practice timelines (Card review row); length and on/off in Training settings; once a day across your devices.
- Finish a lesson later: your place is saved after every step, on every computer. Finish later closes it; the lesson
  picks up where you left off at the next time (or Continue lesson on the Training page). Whatever's left must be
  finished at the day's last lesson time.
- Lessons finished on another computer are noticed within a couple of minutes: no more reminders here, and an open
  lesson says it's done and can be closed.
- Freedom stays signed in: Focus renews Freedom's sign-in when it runs out, and with Keep me signed in (when you
  connect) it signs in again by itself, through restarts and updates. Freedom refusing one request no longer signs
  you out.
- Freedom joins a focus session partway: starting the day in the middle of a session, or Focus reopening during one
  (after a restart or an update), gives Freedom exactly the minutes left in that session.
- Default block lists for focus sessions (Settings, Planning): every focus session Focus fills in, including ones
  you add yourself, starts with them; remove them from any session while planning. They're now the same on all your
  synced computers, and changing them offers to update days you've already planned.

## 1.6.1

- Training has its own place in the sidebar, with an icon like the other pages, and opens inside the main
  window.
- Training opens on a calm board of your books (Add a book for more). Click a book for its lessons,
  chapters, notes, exams and hidden questions; ← All books goes back. Settings for all of Training (lesson
  times, block lists, page view, review, All-books practice) are behind the gear at the top right.
- Chapter packs: a lesson part is now found even when the book's heading is written a little differently
  (for example "Lists, Councils, and Criteria"), so the chapter 1 pack imports without problems.

## 1.6.0

- Fixed: Greek practice rounds came up at times other than the ones on the practice timeline. Outside
  sessions, rounds used the separate "every so many minutes" setting and ignored the timeline. Now a
  practice with timeline times comes up at exactly those times, in and out of sessions.
- Block lists page redesigned: rows highlight the open list, with no trash icons in the list (right-click a
  row to open or delete it); the editor has a large name, a Delete list button beside it, and Apps and
  Freedom web blocking as clean sections with pill-shaped app chips.
- Training: highlight while reading (select text, pick yellow, green or blue, or Highlight + note), a
  Highlights and notes view with Show in the book, Edit note and Remove, and Export to Markdown.
- Training: Page view (turn pages like a book) or scrolling, remembered.
- Training: the book's pictures and maps appear in the reading (choose the book file again once, with
  Replace…, to pick them up).
- Training: unit exams after each part of the book (set the chapters where parts end on the Training page)
  and a final exam; passing the final finishes the book.
- Training: All-books practice for finished books, popping up at its times or opened from the Training page.
- Practice timelines: Book lesson and All-books rows, so your three lesson times sit on the same timeline as
  Greek practice.

## 1.5.0

- Training, the book course (desktop): Training in the sidebar (or the tray menu) opens the course page.
  Choose your book's Word file once; the reading always comes from it, word for word, with headings,
  italics and shaded sidebars. Import chapter packs (Import, or drag a .focuspack onto the page); Focus
  checks each one against your book before adding it.
- Each lesson: warm-up guesses, the whole reading with no timer, review of what's due from earlier lessons
  (this book only), practice questions with Sure or Guessing (double-click an answer for Sure), memory cards
  that flip when clicked (missed ones come back later in the lesson), and the chapter quiz, including
  putting events in order. Every answer has Show where this is in the text, Hide and Flag.
- Three lesson times a day: the first two pop up and can wait; at the last, the lesson opens and can't be
  closed until finished (quitting Focus moves it to tomorrow). Lessons wait for meetings and lunch, and the
  first two for a break. Chosen block lists (apps and Freedom) are on during lessons.
- Progress is kept per device in the synced Focus folder (training), so a lesson finished on one PC counts
  on the others.
- Not in this build yet: highlights and notes, page view, phone and Boox, all-books practice, the course on
  the practice timelines, unit and final exams.

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
