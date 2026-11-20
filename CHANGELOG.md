# Changelog

## 1.3.0 — 2026-11-21
- **DiscordUnblock is now Burrow.** Same app, new name. Your settings, method and Start with Windows carry over automatically.
- **Fixes itself.** Every few minutes Burrow checks that Discord still gets through. If GoodbyeDPI stops or your provider changes its blocking, Burrow restarts it or finds a method that works, and tells you.
- **Restart Discord in one click.** If Discord was already open when you turned the bypass on, a button restarts it so it reconnects through the bypass.
- **Choose your DNS.** Auto picks the most private server that gives Discord's real address (AdGuard, Quad9, Cloudflare, Google or Yandex), or pick one yourself.
- **Türkçe ve Русский.** Burrow and its installer follow your Windows language, or pick one from the title bar.
- **Safer downloads.** GoodbyeDPI is pinned to a known version and checked against its checksum before it runs.
- **First start finds the best method** for your network automatically.

## 1.2.0 — 2026-11-20
- **Automatic updates.** DiscordUnblock checks for a new version once a day, shows what's new, and installs it with one click, then reopens with the bypass back on. Every update is signed by Coursion Studio and checked before it runs.
- **Check for updates any time** from Options or the tray menu, or turn automatic checks off.
- **Version number in the title bar**, so you can see which version you're on.

## 1.1.0 — 2026-11-20
- **Discord connects reliably.** The new default bypass method got every connection to Discord through in testing, where the old one let about half fail.
- **"Find best method" measures instead of guessing.** It makes 12 real connections with each method and keeps the one that works best on your network.
- **Free and open source.** The app's source is now public under the MIT licence.
- **New setup.** A cleaner installer with Start-with-Windows and open-when-done options, and an uninstaller that removes everything, including the network driver.

## 1.0.0 — 2026-11-20
- **First release.** One button to unblock Discord without a VPN, a live connection check, five bypass methods, and a tray icon.
