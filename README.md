# TgJelly

TgJelly puts a Telegram bot inside your Jellyfin server. It sends notifications to the chats, groups and channels you choose.

It tells you about new media (movies, episodes, albums, music videos, books), security events (failed logins, lockouts, new users), and server events (startup, updates, plugin changes, failed tasks). You set it up from the Jellyfin dashboard or from the bot itself.

<!-- screenshot: a new-movie notification in Telegram -->

## Requirements

- Jellyfin 10.11 or Jellyfin 12.
- A Telegram account, to create the bot.

## Install

1. In Jellyfin, open Dashboard, Plugins, Repositories, and add this repository:

   ```
   https://mrsnowball-dev.github.io/tgjelly_plugin/manifest.json
   ```

2. Open the plugin Catalog, install "TgJelly", and restart Jellyfin.

The repository has one build for Jellyfin 10.11 and one for Jellyfin 12. Jellyfin picks the right one.

## Set up

1. Open @BotFather in Telegram and send `/newbot`. Copy the token it gives you.
2. Restrict access to your bot in BotFather's Mini App, so that only the people you want can use it.
3. In Jellyfin, open Dashboard, TgJelly, Telegram. Paste the token.
4. Press **Open @bot**. Telegram opens the bot.
5. In the bot, pick a chat, group or channel with Telegram's own picker. Then pick a preset.

<!-- screenshot: the Telegram tab after the token is accepted -->

You never type a chat ID. The Advanced section of the dashboard has manual options for the rare case where the main way fails.

## Events and presets

Each destination has event switches. A preset sets them for you:

| Preset | What it sends |
|---|---|
| Everything | All events. |
| New media | All media events. |
| Admin alerts | All security events except successful logins, plus all server events. |
| Custom | Only the switches you turn on. |

You can also limit a destination to some libraries, send media, security or server events silently, pause a destination, and send a test message.

<!-- screenshot: the destination editor with the event switches -->

## Custom templates

You can replace the text of a message with your own template. Templates are optional and live in the Advanced section. The poster and buttons keep following their own switches. See [docs/templates.md](docs/templates.md) for the syntax and the fields you can use.

<!-- screenshot: the custom template editor with its preview -->

## Privacy

- The bot talks to Telegram only. The dashboard page never calls Telegram itself.
- The update check contacts `api.github.com`, and only while at least one destination subscribes to the update event. Otherwise TgJelly makes no other network calls. It uses the same proxy as the bot.
- The bot token and proxy password are never shown again after you save them, and they are removed from logs and reports.
- TgJelly keeps its files in `plugins/configurations/TgJelly/` inside your Jellyfin data folder. Uninstalling the plugin leaves them there.

## Troubleshooting

- **Telegram is blocked on your network.** Set a proxy under Dashboard, TgJelly, Advanced.
- **The dashboard page is blank or its buttons do nothing.** The page is an ES module. A strict Content Security Policy, or Cloudflare Rocket Loader, can break it. Disable Rocket Loader for the Jellyfin host and allow the page's scripts in your CSP.
- **"Another app is polling" (409).** Something else is using the same bot token, such as another bot program. Stop it, or press **Take over** on the Status tab. The same button fixes a webhook that was set on the bot.
- **"Another Jellyfin process is using TgJelly's data folder".** Two servers must not share one config folder. This is not supported. Give each server its own.
- **A destination shows "Photos blocked" or "Needs permissions".** Give the bot the rights it needs in that chat, then press **Check again**.

## Build from source

You need the .NET 9 SDK and the .NET 10 SDK, Python 3, and Node 22 for the page tests.

```
dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release
dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build
python3 build/package.py --release 0.0 --output artifacts
```

The last command writes both zips and `manifest.json` to `artifacts/`.

Maintainers: after the first release, enable GitHub Pages from the `gh-pages` branch (Settings, Pages), or the install URL above will not work.

## License

GPL-3.0. See [LICENSE](LICENSE).
