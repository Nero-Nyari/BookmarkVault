<img src="tub.png" alt="BookmarkVault" width="128" />

# BookmarkVault

**English** | [简体中文](README.md)

A **local-first** bookmark manager for Microsoft Edge on Windows (WPF / .NET 8).

It imports your Edge bookmarks into a desktop app where you can organize, probe and audit them. **Everything stays on your own machine** — no sign-in, no cloud sync, no upload of any kind.

---

## Features

### Import & management

- Auto-detects your Edge profile and imports bookmarks, merging by GUID / URL; works even while Edge is running
- Add URLs by hand inside the app, pasting multiple lines at once (one per line)
- Every bookmark is tagged with how it got in (Edge sync / manual), and can be filtered by that
- Edit title and notes, delete bookmarks (only the local entry is removed — your Edge bookmark file is never touched)

### Organizing

- **Two category systems**: project categories (created inside the app; renameable and deletable) and browser categories (mirrored from your Edge folder structure, read-only)
- A URL can belong to several categories at once — tick them in the context menu, or drag the item onto a category folder
- **Organize board** view: drag bookmarks from the sidebar onto category folders to file them
- **Privacy mode**: move bookmarks to the private side and they disappear completely from the normal list; categories on the two sides stay isolated
- Auto-grouping: import the Edge folder structure as editable categories, or group by detected site type
- Filter panel: view / cover / category / source / reachability / security level / site type / TLD — OR within a group, AND across groups, and the condition persists across restarts
- Six sort modes: default, recently added, most visited, name, status, security level

### Probing & security

- Concurrent HTTP probing: status code, final URL, redirect chain, timeout and certificate validity, cached for 24 hours, cancellable at any time
- Site type detection from a built-in local rule set covering 15 categories
- Security audit: HTTPS, certificate, raw IP host, non-standard port, risky TLD, punycode spoofing, phishing keywords, URL shorteners, cross-root redirects
- **Malicious URL database**: pulls the public plain-text feeds from [URLhaus](https://urlhaus.abuse.ch/) and [OpenPhish](https://openphish.com/) — no API key needed — and labels each hit with its source in the detail page. You can also add your own entries (`example.com` for the domain and subdomains, `https://example.com/bad` for a path prefix, `*.example.com` for subdomains only). Deleting a feed entry adds it to an ignore list so the next refresh won't bring it back

### Interface

- Four main views: Steam-style cover wall, compact card wall, overview page, organize board
- Detail page: hero cover, three-column info, reachability status, security findings, saved accounts for that site, related bookmarks
- Covers: import your own, or fetch `og:image` / `twitter:image` automatically; card covers can be cropped separately to 2:3
- Icons: favicon fetch and cache, falling back to a colored initial block
- Choose which browser opens your links; the context menu can temporarily override it
- Customizable keyboard shortcuts
- Steam dark theme; the UI language is Simplified Chinese

### Account vault

- Keep accounts for your sites in one place
- Passwords are encrypted with Windows DPAPI (bound to your Windows account); opening a site can optionally remind you
- One-click export to a spreadsheet: tick the accounts you want (select all / none / invert) and export to **CSV or Excel (`.xlsx`)**; with nothing ticked, everything is exported
- "Include plaintext passwords" is an opt-in toggle, off by default; turning it on warns you that DPAPI encryption will be lost

---

## Requirements

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft Edge (only needed for importing bookmarks)

## Build & run

```bash
git clone https://github.com/Nero-Nyari/BookmarkVault.git
cd BookmarkVault

# run in development
dotnet run

# or just build
dotnet build -c Release
```

The executable lands in `bin/Release/net8.0-windows/BookmarkVault.exe`.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+F` | Focus the search box |
| `Ctrl+R` | Sync Edge bookmarks |
| `F5` | Probe the currently filtered list |
| `Ctrl+E` | Cycle through the main views |
| `Esc` | Step back: close the topmost overlay, else clear the search box, else deselect |

All of these can be rebound or cleared on the Settings page, and the actions that ship without a shortcut (privacy mode, filter panel, adding a URL, opening each page, and so on) can be assigned one. Changes take effect and are saved immediately.

## Where your data lives

Everything is under `%APPDATA%\BookmarkVault\`:

| File / folder | Contents |
| --- | --- |
| `library.json` | Bookmarks, categories, accounts, settings |
| `probe-cache.json` | Probe results cache (safe to delete) |
| `security/blacklist.json` | Malicious URL rules and ignore list |
| `covers/` | Cover images |
| `icons/` | Favicon cache |
| `app.log` | Runtime log |

It is all plain JSON — delete a file to reset that part. **No user data is in the repository**, and `.gitignore` blocks those names as a second line of defence.

## Privacy

- Bookmarks, categories, tags, notes, covers and accounts never leave your machine
- Passwords are encrypted with Windows DPAPI, bound to your Windows account; you'll need to re-enter them after moving to another PC or reinstalling Windows
- The only outbound requests are: one HTTP request to a site when you probe it, and downloading the two public plain-text feeds when you refresh the malicious URL database. **The domains you probe are never sent to a third party**
- The app never writes to your Edge bookmark files — it only reads them

## Project layout

```
BookmarkVault/
├── App.xaml(.cs)           Entry point, dependency wiring, global exception handling
├── MainWindow.xaml(.cs)    Main window, context menus, drag-and-drop, shortcut dispatch
├── AssemblyInfo.cs         Assembly attributes
├── Converters/             Value converters
├── Models/                 Data models (bookmarks, categories, accounts, blacklist, settings)
├── Services/               Edge reader, HTTP probing, security audit, blacklist, covers, icons, account export, storage
├── Themes/                 Steam dark theme resources
├── ViewModels/             MVVM view models
└── Views/                  Standalone pages (accounts, URL database, settings)
```

## Tech stack

- WPF / .NET 8 (`net8.0-windows`)
- MVVM via [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) 8.4.2 — the project's **only** NuGet dependency
- Plain JSON storage with atomic writes and self-healing on corruption

## License

[MIT](LICENSE)
