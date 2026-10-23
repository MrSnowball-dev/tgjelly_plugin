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
3. In Jellyfin, open the Dashboard and pick **Telegram** in its menu. Paste the token.
4. Press **Open @bot**. Telegram opens the bot.
5. In the bot, tap **Add to a channel** or **Add to a group** and choose the chat in Telegram. Then pick a preset.

<!-- screenshot: the Telegram page after the token is accepted -->

You never type a chat ID. The Advanced section of the dashboard has manual options for the rare case where the main way fails.

The custom icons on the bot's buttons and headings show only when the bot owner's Telegram account has Premium; otherwise the bot shows plain labels.

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

## The dashboard

The **Telegram** page in the Jellyfin Dashboard has a sidebar of sections. On a phone it becomes a row of tabs.

- **Overview:** the connection state, notices, the bot, and a summary of your destinations and templates. With no bot connected, it starts with Connect.
- **Destinations:** the chats that get messages, with their events, libraries, silent groups, pause, and test.
- **Messages:** the public URL and what each message shows.
- **Templates:** the editor for every message.
- **Advanced:** proxy, batching, update checks, adding a chat by ID, and diagnostics.

There are no Save buttons. Every setting saves itself, and a short cue beside it says what happened. "Saved" fades after two seconds. "Not saved:" stays, with the reason, until you fix the value. A value that is not valid is never saved. Buttons are only for actions, like Remove, Send test and Reset.

## Message templates

Every message is a template, and you can change any of them. Open the event in the dashboard's Templates section. The editor shows the built-in template, so you start from what you already get. A live preview shows how it will look in Telegram. Your changes save themselves a second after you stop typing, unless the template has errors. Press Send test to send it to a destination, and Reset to go back to the built-in one. The poster and buttons keep following their own switches. See [docs/templates.md](docs/templates.md) for the syntax, the fields you can use, and the built-in templates.

<!-- screenshot: the Templates section with its live preview -->

## Privacy

- The bot talks to Telegram only. The dashboard page never calls Telegram itself.
- The update check contacts `api.github.com`, and only while at least one destination subscribes to the update event. Otherwise TgJelly makes no other network calls. It uses the same proxy as the bot.
- The bot token and proxy password are never shown again after you save them, and they are removed from logs and reports.
- TgJelly keeps its files in `plugins/configurations/TgJelly/` inside your Jellyfin data folder. Uninstalling the plugin leaves them there.

## Troubleshooting

- **Telegram is blocked on your network.** Set a proxy in the Advanced section of the **Telegram** page in the Dashboard.
- **The dashboard page is blank or its buttons do nothing.** The page is an ES module. A strict Content Security Policy, or Cloudflare Rocket Loader, can break it. Disable Rocket Loader for the Jellyfin host and allow the page's scripts in your CSP.
- **"Another app is polling this bot".** Something else is using the same bot token, such as another bot program or a second Jellyfin server. Stop that app. TgJelly connects again by itself once it has stopped.
- **"Another app set a webhook for this bot".** Press **Take over** on the **Telegram** page in the Dashboard. It removes the webhook, so TgJelly can receive updates again.
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
