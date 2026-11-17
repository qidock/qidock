# QiDock

QiDock turns the Windows 10 and 11 taskbar into a floating glass dock with a widget bar of its own: music, weather, calendar, AI limits, currency, timers, notes, reminders and more, grouped into profiles you switch with one click. Cards and panels grow straight out of the dock, notifications arrive as banners at the top, and the whole thing is built to stay light: a low-memory mode keeps it near 140 MB, and widgets sleep while they are off screen.

---

## Features

- **Floating Dock:** Smooth rounded pill container (`CornerRadius="18"`) with prominent high-resolution application icons (32 DIP base, extracted at up to 128x128), translucent smoked glass material, specular top highlight, and ambient shadow.
- **Continuous Fisheye Magnification:** Smooth wave scaling across hovered and adjacent icons, pushing neighbours apart without layout jitter. It is off by default and can be turned on, with its size set from 110% to 220% (Settings → Appearance). When it is on, a small note under the switch says it uses a little more processor time, and, while low-memory mode is on, that it can stutter and that turning low-memory mode off makes it smooth at about 300 MB more memory. No bubble or box appears behind the hovered icon; with magnification off, the tile under the pointer rises a few pixels instead.
- **Multi-Window Live Thumbnail Previews:** Interactive live DWM window preview cards for open applications with one-click window activation and individual window close controls. A preview opens only after the pointer rests on an icon for half a second; while one is open, moving to the next icon switches at once.
- **Smart Auto-Hide (IntelliHide):** Automatically slides down off-screen when an active or maximized window overlaps the dock, and stays visible on the desktop. Smoothly reveals when the mouse touches the bottom screen edge.
- **Drag-and-Drop Reordering:** Reorder dock icons interactively with persistent order saved automatically.
- **Dock Pinning:** Pin running applications or unpin shortcuts directly from the context menu. Classic taskbar shortcuts are imported from the user's Windows pin folder.
- **Native Windows Jump Lists:** View recent/frequent destinations and published application tasks directly in the context menu via lightweight STA Shell reading.
- **Crash Recovery & State Journaling:** Automatically restores the native Windows taskbar on exit or unexpected shutdown using a local state journal (`taskbar-state.txt`). Reconnects on Explorer restarts (`TaskbarCreated`).
- **Dedicated Settings Window:** A window with custom title-bar controls. Customize alignment (Left, Center, Right), theme (Dark/Light), auto-hide, animations, magnification, previews, and Windows startup. Every switch says under its name what it does. A live picture of the dock sits above the tabs: it redraws on every change (edge, position, look, opacity, widget placement, auto-hide, magnification) before anything is applied, its icons magnify under the pointer at the chosen size, and resting on one shows the window preview after the same half second. Nothing on screen changes until Apply.
- **Glass & Surface Opacity:** Settings use native Desktop Acrylic on Windows 11 build 22621 or later. Adjust background opacity from 60–100% for settings, the dock, and context menus without fading text or icons. Changes preview live, save on Apply, and revert when discarded. Unsupported Windows versions and high-contrast mode use an opaque settings surface.
- **Top Widget Bar:** The widgets live in a glass bar at the top of the screen, apart from the app dock. It stays on screen while the desktop is clean (no app window open on the primary monitor), steps aside as soon as a window is open, and can be brought down by resting the pointer on the top edge. While hidden, the widgets pause their per-second timers. Right-click the bar for the edit mode (move, remove, add widgets) and other options.
- **Dock Edge:** The dock can stand on the bottom, top, left or right edge of the screen (dock menu "Dock'un kenarı" or Settings → General). Icons stay upright, the running dots sit on the screen-edge side, magnification and hiding work on every edge, and panels, widget cards and window previews open inward. On the left and right edges the widgets stay in the top bar; on the top edge they move into the dock.
- **Widget Placement:** The widgets can also sit inside the dock, left of the apps or right of them (before Downloads and the bin), with a thin divider between the groups. Choose it from the dock menu ("Araçların yeri") or in Settings → Widgets. When the row is wider than the screen, icons and widgets shrink together so they stay in proportion. In the dock the widgets stay live, and each widget's card grows out of the dock the way the bell's list does (the dock keeps still while it stands there). The Control Center tile sits at the end of the row and replaces the Quick controls tile (Wi-Fi, Bluetooth, volume and brightness are all in the Control Center).
- **Widget Profiles:** Tabs under the bar (shown on hover) switch between widget sets such as "Varsayılan", "İş" and "Araçlar"; Ctrl+Alt+Shift+1…9 switch from anywhere (each tab shows its key), and "Kendinizinkini oluşturun" adds one. Each profile keeps its own order and hidden widgets; right-click a tab to rename or delete it, "+" adds one. The **Profile** tile (in the dock or the bar) shows the active profile; a click lists the profiles to switch to, makes a new one from ready names (İş, Kod, Ev, Odak, Oyun, Okul, Tasarım, Araçlar), renames or deletes the active one, and edits its widgets. It is how profiles are reached while the widgets sit in the dock, where the tabs are not shown.
- **Two Desktop Buttons:** The blue tile next to Start goes straight to the clean desktop (open windows step aside, desktop icons hidden, the widgets stay). The orange folder tile goes straight to the desktop with its folders and files; the desktop widgets and the widget bar step out of the way there. Either button switches to the other desktop directly; pressing the same one again brings the windows back.
- **Dock Shortcuts:** Add apps, files, folders or web links to the dock from the dock menu (Kısayol ekle) or by dropping them on the dock. Remove them with "Unpin" like any pinned app.
- **Recycle Bin:** A Recycle Bin tile at the end of the dock shows whether it is empty; drop files on it to delete them (undoable), click to open, right-click to empty.
- **Notifications at the Top:** New Windows notifications slide down from the top center of the screen as small cards of frosted glass (app icon, app name, time, title and text; the desktop behind is blurred and tinted). Up to three stack, newest on top, the others gliding down to make room; they slide back up after a few seconds; hovering keeps them, a click opens the app (or the notification's link), dragging a card up or its × dismisses it. They stay out of the way of full-screen apps. Windows' own pop-up at the bottom right is moved off screen while it shows, so each notification appears once (nothing in Windows' settings changes; turn "Windows balonlarını gizle" off to get it back). Notifications are read from Windows' own notification database (`wpndatabase.db`, through the `winsqlite3.dll` that ships with Windows) on a private copy, so the original is never written.
- **Notification Center:** Clicking the bell on the dock (with a red unread badge) grows the list out of the dock itself: a small bump rises from the bell and swells, with a soft overshoot, into a panel in the dock's own material, joined to the dock by inward curves (the dock leaves out its edge line there and the grown shape draws no rim of its own, so the two read as one piece); clicking outside pulls it back into the bell. The bell tile next to the battery opens it the same way. Ctrl+Alt+← slides a sheet of frosted glass in from the right edge instead (the desktop behind is blurred and tinted, with rounded corners). The sheet is only as tall as its cards (at most about 60% of the screen, then it scrolls smoothly under the fixed title row). Notifications of one app sit in a single stack (the newest card with the others peeking out below, "+N daha"); click the stack and the cards fan out of it, "Daha az göster" folds them back, × clears the group. Click a single card to open its app; hover it for the × that slides it away. Every change moves the cards from where they were to where they go. The gear holds switches for the top banners, hiding Windows' pop-up and a sound.
- **Smooth Motion:** Panels and overlays are see-through without WPF's layered-window mode (which copies every frame back to memory); cards only move and fade, the glass is resized once per change rather than every frame, and each movement starts after its first frame is drawn, so animations stay fluid on large, high-DPI screens. Set the environment variable `QIDOCK_FRAMES=1` to log frame times to `%LOCALAPPDATA%\QiDock\frames.log`.
- **Control Center:** Ctrl+Alt+→, any part of the Quick Controls tile, the battery tile or the round "Denetim Merkezi" button opens the same frosted sheet with tiles: Wi-Fi (with the network name) and Bluetooth toggles, power mode, unread notifications, volume and built-in display brightness sliders, battery, and shortcuts for screen snip, lock, Windows settings and QiDock settings. The chevron on the Wi-Fi row opens the networks in range (signal, padlock, a tick on the joined one); click one to join it, and a network Windows has not joined before asks for its password right there. "Windows ağ listesi" opens Windows' own list (also the way in when Windows does not let apps list networks).
- **All Apps ("Tüm uygulamalar"):** The dock's colored-grid button or Ctrl+Alt+↓ shows every installed app (desktop and Store apps, as in "All apps") as a large-icon grid over the blurred screen (dimmed on Windows 10); type to filter, Enter opens the first match, Esc or a click on the background closes it.
- **QuickSearch:** Alt+Shift+Space or the "Ara" bar tile opens a search box in the middle of the screen for apps and files in your user folders, with arrow-key selection. Simple sums ("12*4+3") show their result (Enter copies it); the last row searches the web.
- **Window Overview:** Ctrl+Alt+↑ shows all open windows side by side as live thumbnails (minimized ones as their app icon); click one to switch to it.
- **Downloads Stack:** The blue arrow next to the Recycle Bin shows the 12 newest files in your Downloads folder; click to open, drag them into other apps, or open the folder.
- **Full-Screen Apps:** While a game, video or presentation fills the screen, the dock and the bar step aside; they come back when another window comes to the front.
- **Light on Memory:** Start-up work, closed overlays and idle periods (two minutes without input) give unused memory back to Windows, so QiDock's working set drops to a few tens of megabytes while you are not using it.
- **Touchpad Gestures:** Windows keeps three- and four-finger gestures for itself, but Settings → Bluetooth & devices → Touchpad → Advanced gestures can map a swipe to a keyboard shortcut — for example four fingers up to Ctrl+Alt+↑ (Window Overview) and down to Ctrl+Alt+↓ (AppGrid).
- **Volume & Brightness HUD:** When the volume or the built-in display's brightness changes, a small card (icon, name, bar, value) grows out of the middle of the dock and sinks back a moment later; short messages ("copied", "focus done") use it too. While the dock is hidden or another panel stands on it, or with "Ses ve parlaklık dock'tan çıksın" off, a pill at the top of the screen shows instead. Brightness comes from Windows' own power-setting notification (no polling).
- **Claude Code Sessions:** Nothing opens by itself. Clicking the Claude ring of the AI limits tile shows, under Claude's usage, the Claude Code sessions of the last 30 minutes on this PC: the project, its current step ("2 dosya düzenleniyor…", "Komut çalışıyor…", or the command's own description) or "Bitti", with an orange dot while working, a grey one when it has gone quiet and a green tick when finished. The list follows the sessions live while the card is open. Only the end of the local transcripts in `%USERPROFILE%\.claude\projects` is read, and only while that card is open (no network, no background watcher). Turn it off with "Claude kartında Claude Code oturumları".
- **Low-Memory Mode:** "Az bellek modu" (Settings → General) draws everything on the processor instead of the graphics card. On integrated graphics the driver's drawing device alone holds about 300 MB as soon as the first window opens; without it the app needs roughly 50 MB (measured with `--memory-probe`). Blurred shadows are left out in this mode: drawn by the processor, a sliding dock with its shadow managed about 34 frames a second, and about 119 without it, as smooth as on the graphics card (measured with `--frame-probe`). The frosted blur behind panels is replaced by a plain tint. It takes effect after a restart, which the settings window offers. Memory use is written to `app.log` a minute after start and every 30 minutes.
- **Looks:** Settings → Appearance → Tema (or the dock menu's theme submenu) switches between 14 looks: Glass (follows dark/light), Black, Lo-fi, Pixel Night, Christmas, Ocean, Sunset, Forest, Graphite, Neon, Retro, Mocha, Paper and Sakura. Each lays its own colors over the dock, the widget tiles, menus and panels.
- **Widgets:** Each one optional and reorderable in Settings → Widgets or the bar's edit mode:
  - **Music:** Current track from any Windows media session (Spotify, browsers, Media Player): cover, title, artist, a progress bar and previous / play-pause / next. Click the tile for a card tinted by the cover's colour: a large cover, seeking, ±10 seconds, previous / play / next and the playing app's volume. With nothing playing the tile stays and says so (it used to disappear); its play button then sends the keyboard's play key, which resumes the player that last had sound.
  - **Weather:** Temperature with today's icon and the next three days (name, icon, high); click for the next hours and a 7-day forecast with temperature ranges. Data from [Open-Meteo](https://open-meteo.com) (no API key).
  - **Battery:** Charge as a ring with the percent inside and a bolt while charging (hidden on PCs without a battery). Click for the battery's own card: a large ring, where the power comes from (charging, plugged in, on battery), the time left, energy saver, and a link to Windows' battery settings.
  - **Background Apps:** Apps that run in the background with an icon by the clock (Windows' notification area) appear in the dock's corner tile. In its card, click an app to open it, right-click for the app's own menu, and × to close it; an app that does not close within a few seconds offers "Zorla kapat" (force close). QiDock keeps a hidden notification-area window of its own for this and passes everything on to Windows'.
  - **Notifications:** A square bell with the red unread badge, always the last tile of the row, in the corner next to the battery; the list grows out of it. While this tile is shown the dock's own bell stays hidden (so there is always exactly one).
  - **CPU & RAM:** Two small rings with the load inside; click for details.
  - **Clock & Calendar:** Large time with the date under it; click for a month view and the selected day's events from any iCal (.ics) address — Google Calendar's "secret address in iCal format", Outlook or iCloud. Common recurrence rules are supported.
  - **AI Limits:** Claude and ChatGPT as two small rings (logo inside, the busier limit's use around it, percent beside). Nothing shows on hover; click a ring for that provider's own card (bars, reset times, this week's curve and pace), and click the other ring to switch to it. Claude uses the Claude Code sign-in on this PC (the same usage endpoint as `/usage`); ChatGPT reads the latest rate-limit snapshot from Codex's local session logs, without network access. Tokens are read from disk only to call their own provider and are never stored.
  - **Quick Controls:** Wi-Fi and Bluetooth status (the capsules, their chevrons and the battery open the Control Center), a volume slider for the default output device (drag, click or scroll; click the speaker to mute) and a battery tile on laptops.
  - **Folder:** The Desktop (or a folder you pick) as a tile; the card shows its files in a grid you can browse, open or drag out.
  - **Search:** A search field that opens QuickSearch (the same search as Alt+Shift+Space).
  - **Color Picker:** Pick any color on screen with a magnifier; saved colors copy their hex code on click.
  - **App Volume:** Volume slider and mute for each app playing sound.
  - **Focus Timer:** Pomodoro timer (15/25/45/60 minutes) with a 5-minute break and a notice when time is up. It is the same timer as the desktop focus widget: start it in one place and both count down together; after each session a break follows (a long one every fourth round).
  - **World Clock:** Up to three cities with their time difference.
  - **To-Do:** A short task list; the tile shows the next open task.
  - **Tools:** Screenshot, clipboard history, emoji panel, calculator and lock screen.
  - **Profile:** The active widget profile; click to switch, add, rename or delete profiles (see Widget Profiles).
  - **Currency Converter:** One unit of a currency in another (1 USD = … ₺) on the tile; the card converts any amount between TRY, USD, EUR, GBP, CHF and JPY (click a currency to step to the next, ⇅ swaps). European Central Bank rates via [Frankfurter](https://www.frankfurter.app), fetched at most every six hours and only while the tile is shown.
  - **Stopwatch & Timer:** A stopwatch to the tenth of a second, or a countdown of 1–60 minutes that chimes and shows a message at the top of the screen when it ends. The display ticks only while running and on screen.
  - **Quick Note:** A small note that is saved as you type (after a short pause); the tile shows its first line.
  - **App Group:** Up to four favourite apps in one small square, picked from the Start menu's app list; click an icon to open the app, right-click (or an empty slot) to edit.
  - **Month / Year Progress:** Dots for the days of the month (or the months of the year, or the hours of the day) and the percent gone; click to switch. It redraws once a minute while shown.
  - **Water / Break Reminder:** Every 20–90 minutes it chimes and reminds you to drink water, take a break or rest your eyes; for water, "İçtim" counts today's glasses and restarts the wait.
  - **Voice Memo:** The red button records from the default microphone, a second press saves a .wav file (16-bit mono) in Documents\Ses Kayıtları; the card lists the latest recordings and opens the folder. It uses Windows' own recorder (MCI).

  The second batch (currency, stopwatch, note, app group, progress, reminder, voice memo) starts hidden in every profile; add them from the edit mode's "Add" list or the dock menu's widget list.
- **Desktop Widgets:** Dark cards placed freely on the desktop, below all windows, and kept there through Win+D. Open the gallery from the dock or bar menu ("Masaüstüne widget ekle…") or from a widget's right-click menu ("Widget ekle…"); it shows live previews by category with the sizes each kind offers. Drag a card to move it (it snaps to the screen edges and to other cards); right-click it to change its size, use its own options or remove it. Sizes: Mini (164×74), Small (164×164), Strip (344×74), Medium (344×164) and Large (344×344). A card fully covered by a window pauses its updates. The 34 kinds:
  - **Time and date:** digital clock, analog clock, flip clock, date, calendar (iCal events), year/month/week/day progress, world clock, timer, stopwatch.
  - **Weather:** weather (now, hours, days), sunrise and sunset, wind with a compass.
  - **System:** CPU (ring or live graph), memory, disk, battery, network speed with a graph.
  - **Media:** music player (artwork, progress, previous/play/next; five sizes) and volume.
  - **Productivity:** to-do list (shared with the bar), sticky note, focus (Pomodoro, the same timer as the bar) and a repeating reminder (drink water, take a break, rest your eyes).
  - **AI:** Claude and Codex (session and weekly usage limits with their reset times, from the same sources as the AI Limits tile) and Gemini (opens the app); each card carries the app's own icon, which opens the app.
  - **Tools:** apps, quick actions, quick settings, web search, file search, emoji, colors and a currency converter (European Central Bank rates via [Frankfurter](https://www.frankfurter.app)).
- **Vector Icons:** Clean vector outline icons (Lucide icon set, 24x24 coordinate space; license in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)) for context menus and dock controls.

## Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- Windows x64 architecture
- .NET 10 Desktop Runtime (or .NET 10 SDK when building from source)

## Run the Release

1. Get the latest `QiDock.exe` from the project's Releases page (or build it yourself, see below).
2. Save it in a folder you control and run it. Installation is not required.
3. QiDock replaces the Windows taskbar with a floating dock.

## Run from Source

With the .NET 10 SDK installed:

```powershell
dotnet run --project QiDock.csproj
```

To create a self-contained portable executable:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\publish-portable.ps1"
```

The output is written to `dist\QiDock.exe`.

Debug builds also accept a few command-line switches used to check the interface without a live session (`--render-ui <folder>`, `--render-desktop-widgets <file.png>`, `--memory-probe`, `--frame-probe`, `--audio-probe`, `--wifi-probe`, `--gallery-probe` …; see `App.xaml.cs`). They are compiled out of Release builds.

## Configuration and Storage

- `%LOCALAPPDATA%\QiDock\settings.json`: Preferences, alignment, theme, auto-hide, pin order, and `SurfaceOpacity` (0.6–1.0, default 0.9). Existing settings files receive the default opacity.
- `Widgets` in `settings.json`: widget order, hidden widgets, profiles, weather city, calendar iCal address, the seconds option, and widget data (folder path, saved colors, to-do items, world clocks, focus length).
- `Shortcuts` in `settings.json`: shortcuts added to the dock (name, target, arguments).
- `%LOCALAPPDATA%\QiDock\desktop-widgets.json`: desktop widgets — kind, size, position and per-widget options (note text, reminder interval, currencies…).
- `%LOCALAPPDATA%\QiDock\notifications.json`: which notifications were read, removed or cleared here. `notifications-copy\` holds the private copy of Windows' notification database.
- `%LOCALAPPDATA%\QiDock\taskbar-state.txt`: Safety journal that preserves the original Windows taskbar state for crash recovery.
- Task Scheduler task `QiDock`: starts the dock at sign-in when "Start with Windows" is on (the registry's `Software\Microsoft\Windows\CurrentVersion\Run\QiDock` is used only if the task cannot be created).
- `%LOCALAPPDATA%\QiDock\app.log`: start, exit, memory and error lines; `toast-windows.log`: what the Windows pop-up hider moved (kept under 64 KB).

## Project Structure

- `Design/`: Shared XAML styles, materials, typography, and dark/light color palettes (`Palettes/`).
- `Models/`: Application data structures (`DockApp.cs`, `DockSettings.cs`, `TrackedWindow.cs`, `WidgetSettings.cs`).
- `Services/`:
  - `TaskbarReplacementSession.cs`: Native Windows taskbar suppression, AppBar registration, and monitor layout.
  - `DockAutoHideController.cs`: Smart auto-hide, window overlap evaluation, and bottom edge trigger.
  - `WindowThumbnailPreview.cs`: DWM live multi-window thumbnail strip and preview cards.
  - `NativeWindowTracker.cs`: Win32 window event hooks and alert state tracking.
  - `ApplicationJumpListService.cs`: Background Jump List reader and task invocation.
  - `WindowsTaskbarPinImporter.cs`: Windows shortcut file importer and resolver.
  - `DockSettingsStore.cs` & `ApplicationPreferences.cs`: Settings persistence and state management.
- `Widgets/`: Widgets (`MediaWidget`, `WeatherWidget`, `ClockWidget`, `AiLimitsWidget`, `SystemWidget`, `BatteryWidget`, `QuickControlsWidget`, `ControlCenterWidget`, `NotificationsWidget`, `TrayWidget`, `FolderWidget`, `SearchWidget`, `ColorsWidget`, `AppVolumeWidget`, `FocusWidget`, `WorldClockWidget`, `TodoWidget`, `ToolsWidget`, `ProfileWidget`, `CurrencyWidget`, `StopwatchWidget`, `NoteWidget`, `AppGroupWidget`, `ProgressWidget`, `ReminderWidget`, `VoiceMemoWidget`), the `WidgetBar` host (shown in `TopBarWindow`) and shared popup/icon helpers (`WidgetUi.cs`).
- `Services/Widgets/`: Widget data sources — Open-Meteo weather, iCal feed parser, AI usage readers, Core Audio volume and per-app sessions, radios and battery.
- `DesktopWidgets/`: Desktop widgets — the per-card window (`DeskWidgetWindow`), layout and snapping (`DeskWidgetManager`), the gallery (`DeskGallery`), the catalog of kinds (`DeskCatalog`), shared data hubs (`DeskHubs`), drawing helpers (`DeskVisuals`) and the kinds themselves (`Kinds/`).
- `MainWindow.Desktop.cs`: Starts the desktop widgets and tells them which cards are covered by windows.
- `Services/Notifications/NotificationFeed.cs`: Reads Windows notifications, tracks read/removed state, and resolves each app's name, icon and window.
- `NotificationBanner.cs` & `MainWindow.Notifications.cs`: The top banners and the dock bell with its badge (`Widgets/NotificationsWidget.cs` has the bar tile); `Services/Notifications/WindowsToastHider.cs` keeps Windows' own pop-up off screen.
- `EdgePanel.cs`, `NotificationCenter.cs` & `ControlCenter.cs`: The panels that slide in from the right edge (`ControlCenter.Wifi.cs` and `Services/Widgets/WifiNetworks.cs`: the Wi-Fi network list).
- `Services/TrayHost.cs`: The hidden notification-area window that collects background apps' icons and forwards everything to Windows' own taskbar.
- `Overlays.cs`: AppGrid, QuickSearch and Window Overview (full-screen overlays); `Services/AppCatalog.cs` lists the installed apps.
- `Services/GpuTransparency.cs`: See-through windows composed by Windows itself (no layered-window copy), frosted glass for the side panels and blur behind AppGrid.
- `MainWindow.Extras.cs`: Global shortcuts, the Downloads stack, full-screen awareness and idle memory trimming.
- `MainWindow.Shortcuts.cs`: Desktop buttons, Recycle Bin and user dock shortcuts.
- `MainWindow.xaml`: Dock UI surface, Start button, application icon items, and context menus.
- `SettingsWindow.xaml`: Tabbed preferences window.

## License

QiDock is distributed under the MIT License. See [LICENSE](LICENSE). The outline icons come from Lucide (ISC); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
