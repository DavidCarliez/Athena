+++
title = "telegram"
chapter = false
weight = 6
+++

## Summary

Athena can use private Telegram bot-to-bot messages for C2 when paired with the `telegram` Mythic C2 profile.

Each running payload requires a dedicated agent bot token. The Telegram controller service requires a separate controller bot token. Enable **Bot-to-Bot Communication Mode** for both bots through BotFather before starting the payload.

The transport splits encrypted Mythic messages into Telegram-safe chunks. Do not reuse one agent bot token across concurrent payload instances because their `getUpdates` calls share one update queue.

Outbound responses remain pending until the controller acknowledges the matching request. This prevents a transient Telegram or controller error from discarding task output.
Mythic can push tasking between agent requests. The controller holds that tasking until the next agent exchange, and Athena also accepts explicitly pushed tasking envelopes.


## Required parameters

- `bot_token`: dedicated agent bot token
- `controller_bot`: controller bot username
- `api_base`: Telegram Bot API base URL
- `message_checks`: maximum polls for a controller response
- `time_between_checks`: long-poll timeout in seconds
- `AESPSK`: Athena message encryption

For a Windows single-file build, Mythic stores the executable directly. The payload shown in the Payloads view is therefore a downloadable `.exe`, not a ZIP archive with an `.exe` filename.


The profile also supports Athena sleep and jitter, an expiration date, a custom User-Agent, and HTTP proxy settings.

## Security considerations

The bot token is embedded in the payload and must be revoked if the payload is recovered. Telegram is not an end-to-end encrypted transport, so keep `AESPSK` enabled.
