# Focus changelog

Each version gets a "## x.y.z" heading. The GitHub build uses the section that matches
<Version> in FocusApp.csproj as the release notes, and Focus shows them when it offers the update.

## 1.7.19

- Today's practice on the Training page is now the practice timeline: one row for each practice, each round a dot in
  the practice's color with its exact time beside it (filled when done, a white ring on the next round). Drag a dot to
  move it, click a row to add a round, right-click a dot to remove it; changes save as your daily times. Every
  "Practice timelines" link now opens it. The Day plan and Dashboard markers use the same colors.
- Round sizes in Training settings, Practice: questions in a Books practice round, cards in a Book flashcards round,
  events in a Timeline round, Greek words and Greek alphabet letters.
- Tasks: the side bar's dock button is now a pop-out style icon.
- The sidebar is icons only: larger icons lined up under the Focus logo and the add-task button. It no longer widens,
  and the daily goal panel that showed when it was wide is gone. Point at an icon to see its name.

## 1.7.18

- Focus draws its own title bar: the notification bell and the ⋯ menu sit right beside minimize, maximize and close,
  so they no longer take room from the page. Drag the bar to move the window; double-click it to maximize.
- The ⋯ menu is a clean panel in Focus's colors, with an icon and shortcut for each page.

## 1.7.17

- Fixed: practice rounds stopped popping up. A check that lets the day's lesson go first could stay on all day (it
  looked at the open book, not the book whose lesson was due) and held every round back. Now it waits at most 10
  minutes after a lesson time.
- Fixed: moving a Books practice, Book flashcards or Timeline round to a few minutes from now didn't make it pop up
  (a round was remembered by its place in the list, not its time).
- New sidebar: Day plan, Calendar, Tasks, Personal, Training and Pomodoro. Click the Focus logo for the Dashboard.
  Settings, Block lists and Stats are in the ⋯ menu at the top right, beside the notification bell. Ctrl+1 is the
  Dashboard, Ctrl+2 to Ctrl+7 the sidebar's pages, Ctrl+, Settings.
- Tasks: collapse the side bar (»), and it becomes a slim strip that opens over the list when you point at it; pin it
  (📌) to keep it open.
- Pomodoro and Quick focus are one: choose the number of sessions (1 is a single quick focus), their length and the
  break, or Keep going until I stop. Optionally name what you're working on.

## 1.7.16

- Today's practice on the Training page: the line now shows just a dot at each round's time (a chain stacks under it),
  with the times and names in a tidy list beneath, so nothing overlaps. The next round is marked in amber.
- "Evening card review" is now called Book flashcards everywhere.

## 1.7.15

- Rename a book: Rename… on its page, or click its cover on the Training page. Its lessons and progress stay with it.
- Cover photos: add one on a book's page (click the cover, or Add a cover photo…). It shows on the book's page, beside
  today's lesson and in your books on the Training page, and syncs to your other computers. Books without one get a
  tile in their color with their first letter.
- Reading: the zoom slider and page bar at the bottom of page view are gone. Ctrl + makes the text bigger, Ctrl −
  smaller and Ctrl 0 puts it back to the usual size; the size is saved, like the size under Aa.
- Today's practice on the Training page: the line now shows just a dot at each round's time (a chain stacks under it),
  with the times and names in a tidy list beneath, so nothing overlaps. The next round is marked in amber.
- "Evening card review" is now called Book flashcards everywhere.
- Includes everything in 1.7.14.

## 1.7.14

- New Training page: today's lesson with one Start button, this week's numbers (pages read, lessons done, practice
  rounds, weeks in a row), today's practice on a line through the day with each round's exact time (rounds at the same
  time form a small chain; done, next and required are marked), a card for each practice with Start, and your books
  with their progress and a Go button for an extra lesson.
- Lessons this week, under your books: drag a lesson to another day (onto a day that has one to make it two), × to
  remove one, + Add to choose a book, ‹ › for the weeks ahead, and Back to my weekly plan. Changes apply to that week
  only. Past days can't be changed; a lesson you remove frees that day; two computers changing the same week keep
  the newest change for each day; a lesson put on a weekend comes up even with weekend practice paused.
- Two lessons a day: each lesson time offers the day's next lesson. At the last time only the first lesson is
  required; a second left undone becomes a catch-up (one at a time).
- Once the day's lessons are done, the other lesson times disappear (no more pop-ups, and they drop off Today's
  practice and the day views).
- Fixed: an extra lesson in a book that isn't today's sometimes opened today's book instead. The open book no longer
  changes in the background, and Start lesson on a book's page always uses that book.
- Timeline practice is its own exercise with its own end page (it used to finish like a lesson). Each event is spaced
  on its own: misplaced ones come back sooner, the further off the sooner; exact placements space out and, after four
  in a row, are learned. Learn (the default) gives what's due plus your last two lessons' events; Review lets you
  pick an era (Persian period, Hasmonean rule, Life of Jesus...) or a span of years. It has its own row on the
  practice timeline and is in the Dashboard's Training menu.
- Timeline browser: every event you've learned on one line you can zoom; click one to read its lesson's passage.
  Open it at the end of a timeline round or from Training settings, Practice.
- Training settings now look like Focus's Settings (a page list, small-capital headings, one row per setting, "Changes
  save as you make them." and Done), and every Training setting is there, Greek practice included (moved from
  Settings).
- The reader now shows Word text boxes once, as a shaded box; captions small and muted under their picture; bulleted
  and numbered lists; tables as grids; Word charts (bar, column, line and pie) drawn from their numbers; and Windows
  drawings (EMF, WMF) as pictures. Choose each book's Word file again to see these (your progress isn't affected).
- Fixed: picking a day in a date picker could freeze Focus's window until it was minimized.

## 1.7.13

- Fixed: "put these in order" questions in a lesson's practice set (and in reviews) showed only the prompt and no
  items, so you couldn't go on. They now work everywhere a question can appear: practice, chapter quiz and review.
  They also look like the other questions now: progress bar, question card, thumbs and the hide/flag menu, and they
  show when they will come back.

## 1.7.12

- Fixed: adding a second book froze Focus and let the new book take over your study days. Switching books in the
  background redrew the Training page, which switched books again, endlessly. Background switches are now silent,
  review counts are cached, and with two or more books and no weekly plan yet, the book you've done most in keeps
  every weekday (Track A) while the others wait for days (Track B).
- Books practice (replacing All-books practice): multiple-choice rounds in the concept quiz's look, with Learn and
  Review at the top right. Learn: what's due today in the day's book. Review: choose any book, then any lesson you've
  done, or All lessons. Turn it on to pop up at its times on the practice timeline, as often as you like.
- Book flashcards (replacing the evening card review): the flip cards, with the same Learn and Review. Each time on
  the timeline is its own round, so it can come up in the evening or more often.
- The multiple-choice questions in lessons and reviews have the concept quiz's look: progress bar, question card,
  answers with round marks, green and red after you answer.
- Today's practice: listed with times on the Training page (on by default), and optionally as small tags in the Day
  plan and Dashboard (off by default). Both are switches in Training settings.
- The Dashboard's Training menu lists Books practice and Book flashcards.
- Modern, themed dropdowns, sliders and radio buttons throughout (Training settings, the reading view and
  elsewhere); the "If you miss a lesson" choices are readable now.
- Fixed: the font list under Aa in the reading view sometimes closed without choosing the font.
- Timeline exercise: a close-enough answer now also shows the exact date and where you placed it.

## 1.7.11

- New meetings in today's calendar are noticed again: Focus asks right away, with a dialog, whether to fit the meeting
  into your day or keep your plan with the meeting shown on top (Settings, Calendar sets ask, fit in or show on top).
  This works while your day runs and on a plan you've made for today.
- Click a meeting shown on top of your day (in the Dashboard, Day plan, Calendar or Home) and choose Add to my day.
- Fitting a meeting in: it starts on time. A focus session or break running when it begins ends right then (or
  at once, if the meeting has already begun). The focus sessions it covers make room and the usual break follows it. A meeting already going
  on when it starts makes it begin when that one ends. Your set break and lunch are skipped that day if the meeting
  covers them completely, or moved just before or after it if it only overlaps them (the only time they move).
  Meetings that touched lunch or another meeting used to be left out without a word; now only one that would cover
  another meeting stays on top, and Focus tells you why.

## 1.7.10

- The dashboard is back to its original look: the round timer, with the Roman numeral and FOCUS SESSION above it while a
  session runs. Extend, Pause, Skip and Stop (and Start my day) now sit to the right of the ring, where the day's totals
  were, so the tasks get more room. The Tasks menu and the small Training menu from 1.7.9 stay.
- The old typed-in concept cards window and the Q&A button are removed; Greek Concepts is the concept quiz (question packs).
- Everything else from 1.7.9 stays: the concept quiz, the timeline exercise without overlapping cards, no task names in
  the dashboard's day view, Today and Tomorrow setting the task's date, and the thumbs buttons fully visible.

## 1.7.9

- Greek Concepts is now a concept quiz, like Biblical Mastery Academy's. Learn mode asks one lesson's 10 to 12 questions
  (from its "What to remember" section); Review mode asks 12 mixed from every lesson so far. Choose the mode on the quiz
  at the start of each round (Learn by default). Questions can have one or several right answers ("Select the correct
  answer(s)"). After Check: a short explanation, the lesson and section, and Open in the lesson, which shows that spot
  in the lesson text. Rounds come up at the Greek Concepts times on your timeline. Settings, Greek practice, Question
  packs: import a lesson's pack and choose which lesson Learn mode uses (the most recent unless you pick another). The
  old typed-in concept cards are retired.
- Dashboard: the ring and its numbers give way to a slim thermometer under the Roman numeral and FOCUS SESSION, with the
  time left beside it and the start and end underneath. The tasks have one title menu (Session, Today, All, Undated),
  and a small book icon opens Training (Greek Concepts, Greek Vocab, Greek Alphabet, Timeline, Book Concepts) over
  everything. The pop-out button has a modern icon.
- Timeline practice on its own (Training menu): events from the chapters you've done.
- The timeline exercise never lets cards cover each other: as many rows as needed, the line moves down to fit, and
  every card stays readable after checking.
- The dashboard's day view no longer shows task names on sessions (the Day plan still does).
- Today and Tomorrow on a task set its date, exactly like the date field.
- The thumbs up and down buttons in practice are no longer cut off.

## 1.7.8

The book lesson pops up like Greek practice:

- At each lesson time, the lesson opens over the screen you're working on (just Focus running is enough), with Start
  lesson (or Continue lesson, if you started it) and Later in the top right, which says when it comes back.
- The day's last lesson time has no Later and can't be closed: it stays until you start the lesson, and Start opens the
  lesson as required, so it has to be finished (including one you half did earlier). Quitting Focus from the tray puts
  it off, but it comes back each time Focus starts that day until the lesson is done; the next day it simply moves to
  the book's next planned day.
- While the lesson popup is showing, Greek practice rounds wait, then follow it back to back.

## 1.7.7

Practice timelines you can rely on:

- A lesson time you move always comes up at its new time. (Lesson reminders were remembered by their place in the day,
  so moving one that had already come up today kept it quiet.)
- Same-time practice is a chain: when several things share a time, they come up back to back in the order of the rows
  (the book lesson first, then Words, Alphabet and Concepts), each opening the moment you finish the one before. They
  never open on top of each other; if you don't start the lesson within 10 minutes, the rounds go ahead.
- Everything syncs: the book lesson, card review and All-books times now travel with the Greek timelines in the shared
  timeline file, with the newest change winning, and every computer re-reads it within two minutes. Switching to the
  day's book no longer rewrites the book's settings file, so an older computer can't put back old times.
- The Practice timelines window keeps just its title and Pause on weekends; the explanation is in the "?" beside the title.

## 1.7.6

Training follows a weekly plan, so several books can run side by side:

- Weekly plan (Training settings): choose each book's weekdays and its role (Track A, Track B or Elective). Weekends stay
  free: no lessons and no review. With one book (or before you plan), your book takes every weekday, as before. The
  order of your books sets what comes next in each track.
- The Training page shows Today (that day's book and lesson, or "No lesson ready: import chapter N" with an Import
  button, or the weekend), Review across all your books (how many are due in each, and new cards waiting), This week
  (each weekday's book; finished days marked with a solid circle and check, missed days in amber), and Your books
  (role, days, progress and what comes next).
- Missed a lesson? An optional Catch-up card offers it the next weekday ("Do it now" or "Let it go"); or choose "Just
  carry on" in the plan.
- Lesson times now offer the day's planned book.
- Fixes 1.7.5, which didn't build: its review changes (graduating questions, retiring known concepts, credit for late
  right answers, and the daily review time) are all in this version.

## 1.7.5

Review that keeps up with you, so you can learn the facts thoroughly and still move on through the book:

- Memory cards are the long-term facts (people, dates, places, ideas): they keep the long schedule (1, 3, 7, 14, 30,
  60 and 120 days). Practice and quiz questions check that you understood the reading: they come back once, three
  days later, and graduate if you get them right (wrong, and they come back the next day until you do).
- Known for good: once a concept has been right on six separate days spread over at least 90 days, its questions leave
  daily review; a memory card that comes through its 120-day review graduates too. After each answer you'll see
  "learned: done with daily review" when that happens.
- Remembered for longer than planned: answering an overdue question right gives it a longer next gap, based on how long
  you actually remembered it, so a backlog clears itself instead of snowballing.
- Daily review time (Training settings, about 10 minutes): new lessons' memory cards join your review only while it fits
  in that time. After a busy stretch they wait their turn and join a few a day; lessons always go ahead. The book's page
  shows how many are waiting. Everything you'd already learned stays in your review as it was.

## 1.7.4

- Settings, redesigned: each section has a small heading in capitals, and each setting is a row with its name, what it
  does and a toggle on the right, with a thin line between rows. Features shows how many are on ("8 of 12 on") in the
  list of pages. At the bottom: "Changes save as you make them." and Done, which takes you back to where you were.
  (The Save buttons on each page are gone; Reset this page stays.)
- Toggles everywhere: every on/off choice in Focus (Settings, sessions, block lists, Pomodoro, Greek practice, Training,
  the floating timer and dialogs) is now a toggle switch in your theme's colors. The circles you tick to finish a task
  stay circles.
- Reading, like Kindle: the lesson's parts moved out of the bar across the top into Contents, a button at the top left
  that opens them in a panel over the page (click a part to go there; click outside it or press Esc to close).
- Reading without distractions: click the middle of the page to hide everything but the text (the bars, progress,
  arrows and settings panel); click the middle again or press Esc to bring them back. Clicking the left or right side
  still turns the page, and selecting text, links and highlights work as before.

## 1.7.3

Includes 1.7.2 (never released separately):


- Reading, redesigned: a reading progress bar with time left, the lesson's parts as buttons that jump to each part,
  and a reading-settings panel beside the text (folds away with Aa): layout (scroll, pages or two columns), font,
  size, background, line spacing, width and alignment. In page layouts, arrows beside the page turn it too.
- Answers, cleaner: the other choices fade, and one card holds the verdict, when it comes back, the explanation,
  See it in the book and ··· (Hide, Flag). Enter goes on.
- Thumbs up and thumbs down beside every question and memory card: up brings it back sooner (and first in the
  evening card review), down later. It always stays in the spaced-repetition schedule.
- Timeline in every lesson: drag events onto a dated line; a bubble shows the year under the pointer; nudge the
  selected card a year at a time with ‹ › or the arrow keys (good on a touchscreen); Finished shows what was right,
  with arrows to the real dates for the rest. Chapter packs carry the dated events (chapter 2's updated pack has them).
- The book's page is now a dashboard: today's lesson with its stages, book progress, the evening review, four
  numbers, and each chapter's progress.

- Tasks: small Today and Tomorrow tags on every task (not just on hover), to add it to that day in one click.
- Reading: Scripture references in the book's text link to Logos.
- Page view: only the strip beside the middle of the page turns it, not the top near the window buttons or the
  page bar at the bottom.
- Memory cards: the instructions under the card are gone.
- Lessons always run in this order: warm-up, reading, practice (this lesson's questions), memory cards (this
  lesson's), the chapter quiz when a chapter ends, then review (cards first, then multiple choice, from every lesson
  of the book done so far). The evening card review comes later in the day.
- Extra lessons any time (an evening, a weekend): Do an extra lesson on the Training page starts the next one with
  no questions asked; it counts, and the next weekday offers the one after it.

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
- No catching up: if you're away while several rounds' times pass, only one round of each practice comes up when
  you're back; the earlier ones are skipped.
- Daily reset at 4:30 PM (change it or switch it off in Greek practice or Training settings): practice, lesson and
  review times before it stop for the day, and anything left on screen while nobody's at the computer (untouched for
  five minutes) closes; a lesson closed this way moves to tomorrow. Times you put after the reset still come up and
  last until midnight, when anything left open from them closes the same way.
- The practice timelines are the same on all your computers: change them on one and the others follow.
- A round done on one computer counts on the others: that round (and any earlier one that day) won't come up there.

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
