using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// An in-process Bot API behind a real <see cref="TelegramBotClient"/>: records every call and answers with
/// scripted or default JSON. Throwing from a handler simulates a transport failure.
/// </summary>
public sealed class FakeBotApi : HttpMessageHandler
{
    public const string Token = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ";
    public const long BotId = 123456789;
    public const string BotUsername = "tgjelly_test_bot";

    private readonly ConcurrentQueue<BotApiCall> _calls = new();
    private readonly ConcurrentDictionary<string, Func<BotApiCall, CancellationToken, Task<string>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, string> _members = new();
    private int _messageId;
    private int _fileId;
    private int _topicId = 100;

    public IReadOnlyList<BotApiCall> Calls => _calls.ToArray();

    public static string Ok(string resultJson) => "{\"ok\":true,\"result\":" + resultJson + "}";

    public static string Error(int code, string description, string? parametersJson = null)
        => "{\"ok\":false,\"error_code\":" + code.ToString(CultureInfo.InvariantCulture) + ",\"description\":" + JsonSerializer.Serialize(description)
           + (parametersJson is null ? string.Empty : ",\"parameters\":" + parametersJson) + "}";

    public static string Member(string status, string extraJson = "")
        => "{\"status\":\"" + status + "\",\"user\":{\"id\":" + BotId.ToString(CultureInfo.InvariantCulture) + ",\"is_bot\":true,\"first_name\":\"b\"}" + extraJson + "}";

    public IEnumerable<BotApiCall> CallsTo(string method) => Calls.Where(c => c.Method == method);

    public void On(string method, Func<BotApiCall, string> respond)
        => _handlers[method] = (call, _) => Task.FromResult(respond(call));

    public void OnAsync(string method, Func<BotApiCall, CancellationToken, Task<string>> respond)
        => _handlers[method] = respond;

    public void OnSequence(string method, params string[] bodies)
    {
        var next = -1;
        On(method, _ => bodies[Math.Min(Interlocked.Increment(ref next), bodies.Length - 1)]);
    }

    public void AddChat(long chatId, string type, string title, bool isForum = false, string? permissionsJson = null, string? username = null)
    {
        var json = new JsonObject
        {
            ["id"] = chatId,
            ["type"] = type,
            [type == "private" ? "first_name" : "title"] = title,
            ["accent_color_id"] = 0,
            ["max_reaction_count"] = 11,
        };
        if (isForum)
        {
            json["is_forum"] = true;
        }

        if (username is not null)
        {
            json["username"] = username;
            _chats["@" + username] = json.ToJsonString();
        }

        if (permissionsJson is not null)
        {
            json["permissions"] = JsonNode.Parse(permissionsJson);
        }

        _chats[chatId.ToString(CultureInfo.InvariantCulture)] = json.ToJsonString();
        if (username is not null)
        {
            _chats["@" + username] = json.ToJsonString();
        }
    }

    public void SetMember(long chatId, string memberJson) => _members[chatId] = memberJson;

    public ITelegramBotClient CreateClient()
        => new TelegramBotClient(new TelegramBotClientOptions(Token, "http://fake.test") { RetryThreshold = 0 }, new HttpClient(this));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var method = request.RequestUri!.AbsolutePath.Split('/')[^1];
        var call = new BotApiCall(method, await ReadBodyAsync(request.Content, cancellationToken).ConfigureAwait(false));
        _calls.Enqueue(call);

        if (method == "getUpdates")
        {
            // A real getUpdates never answers instantly (it long-polls). Without this, a scripted "[]" makes the
            // poll loop spin synchronously, and under FakeTimeProvider.Advance it would spin on the test's thread.
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        var body = _handlers.TryGetValue(method, out var handler)
            ? await handler(call, cancellationToken).ConfigureAwait(false)
            : await DefaultAsync(call, cancellationToken).ConfigureAwait(false);

        var parsed = JsonNode.Parse(body)!;
        var status = parsed["ok"]!.GetValue<bool>() ? HttpStatusCode.OK : (HttpStatusCode)parsed["error_code"]!.GetValue<int>();
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is MultipartFormDataContent multipart)
        {
            var result = new JsonObject();
            foreach (var part in multipart)
            {
                var disposition = part.Headers.ContentDisposition!;
                var name = disposition.Name!.Trim('"');
                if (disposition.FileName is { } fileName)
                {
                    result[name] = "file:" + fileName.Trim('"');
                    continue;
                }

                var text = await part.ReadAsStringAsync(ct).ConfigureAwait(false);
                result[name] = TryParseJson(text) ?? JsonValue.Create(text);
            }

            return result;
        }

        if (content is null)
        {
            return [];
        }

        var json = await content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json)!.AsObject();
    }

    private static JsonNode? TryParseJson(string text)
    {
        if (text.Length == 0 || !(text[0] is '{' or '[' or '-' || char.IsDigit(text[0])))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ChatKey(BotApiCall call) => call.Body["chat_id"]?.ToString() ?? string.Empty;

    private string MessageJson(BotApiCall call, bool photo)
    {
        var chatId = long.TryParse(ChatKey(call), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1;
        var message = new JsonObject
        {
            ["message_id"] = Interlocked.Increment(ref _messageId),
            ["date"] = 1_790_000_000,
            ["chat"] = new JsonObject { ["id"] = chatId, ["type"] = chatId > 0 ? "private" : "supergroup" },
        };
        if (photo)
        {
            var n = Interlocked.Increment(ref _fileId).ToString(CultureInfo.InvariantCulture);
            message["photo"] = new JsonArray(
                new JsonObject { ["file_id"] = "small-" + n, ["file_unique_id"] = "us" + n, ["width"] = 90, ["height"] = 90 },
                new JsonObject { ["file_id"] = "file-" + n, ["file_unique_id"] = "u" + n, ["width"] = 800, ["height"] = 1200 });
            message["caption"] = call.Body["caption"]?.ToString();
        }
        else
        {
            message["text"] = call.Body["text"]?.ToString();
        }

        return message.ToJsonString();
    }

    private async Task<string> DefaultAsync(BotApiCall call, CancellationToken ct)
    {
        switch (call.Method)
        {
            case "getMe":
                return Ok("{\"id\":" + BotId.ToString(CultureInfo.InvariantCulture) + ",\"is_bot\":true,\"first_name\":\"TgJelly test\",\"username\":\"" + BotUsername + "\"}");
            case "getWebhookInfo":
                return Ok("{\"url\":\"\",\"has_custom_certificate\":false,\"pending_update_count\":0}");
            case "getUpdates":
                await Task.Delay(20, ct).ConfigureAwait(false);
                return Ok("[]");
            case "getChat":
                return _chats.TryGetValue(ChatKey(call), out var chat) ? Ok(chat) : Error(400, "Bad Request: chat not found");
            case "getChatMember":
                if (!_chats.TryGetValue(ChatKey(call), out var known))
                {
                    return Error(400, "Bad Request: chat not found");
                }

                var chatId = JsonNode.Parse(known)!["id"]!.GetValue<long>();
                return Ok(_members.TryGetValue(chatId, out var member) ? member : Member("left"));
            case "sendMessage":
            case "editMessageText":
                return Ok(MessageJson(call, photo: false));
            case "sendPhoto":
            case "editMessageCaption":
                return Ok(MessageJson(call, photo: true));
            case "createForumTopic":
                return Ok(new JsonObject
                {
                    ["message_thread_id"] = Interlocked.Increment(ref _topicId),
                    ["name"] = call.Body["name"]?.ToString(),
                    ["icon_color"] = 7322096,
                }.ToJsonString());
            default: // deleteMessage and every other boolean method
                return Ok("true");
        }
    }
}
