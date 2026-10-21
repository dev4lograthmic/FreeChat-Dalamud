using System.Text;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using Google.Protobuf;

namespace FreeChat.Cli;

internal static class Program
{
    private const string Version = "0.2.0";

    public static async Task<int> Main(string[] args)
    {
        // JSON and chat text are UTF-8 on the wire. Keep redirected output
        // UTF-8 as well instead of the Windows console code page.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] is "-v" or "--version")
        {
            Console.WriteLine($"freechat {Version}");
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        try
        {
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch
            {
                "pair" => await PairAsync(options).ConfigureAwait(false),
                "subscribe" => await SubscribeAsync(options).ConfigureAwait(false),
                "send" => await SendAsync(options).ConfigureAwait(false),
                "friends" => await FriendsAsync(options).ConfigureAwait(false),
                "presence" => await PresenceAsync(options).ConfigureAwait(false),
                "ping" => await PingAsync(options).ConfigureAwait(false),
                "decode" => Decode(options),
                _ => throw new CliException($"Unknown command: {command}. Use --help."),
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"freechat: {ex.Message}");
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("freechat: cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"freechat: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> PairAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadOrCreate(statePath, options.Get("device-name"));
        using var cancellation = CreateCancellation();

        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        if (!options.Has("print-json"))
            Console.WriteLine("Pairing complete. Device state saved.");
        return 0;
    }

    private static async Task<int> SubscribeAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadExisting(statePath);
        using var cancellation = CreateCancellation();

        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        await client.SubscribeAsync(
            options.Get("sources"),
            options.Get("keyword"),
            options.Get("regex"),
            options.Has("include-battle"),
            options.Has("exclude-own"),
            options.GetLong("from", 0),
            cancellation.Token).ConfigureAwait(false);

        await client.RunAsync(options.GetInt("count", 0), cancellation.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SendAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadExisting(statePath);
        using var cancellation = CreateCancellation();

        var request = BuildSendRequest(options);
        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        var result = await client.SendAsync(
            request,
            cancellation.Token,
            options.Has("wait-echo")).ConfigureAwait(false);
        return result.Status is SendStatus.Submitted or SendStatus.Echoed ? 0 : 1;
    }

    private static async Task<int> FriendsAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadExisting(statePath);
        using var cancellation = CreateCancellation();

        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        await client.RequestFriendListAsync(
            (uint)Math.Max(0, options.GetInt("page-size", 0)),
            options.Get("owner-key"),
            options.Get("owner-epoch"),
            cancellation.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> PresenceAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadExisting(statePath);
        using var cancellation = CreateCancellation();

        var contentId = (ulong)options.GetLong("content-id", 0);
        if (contentId == 0)
            throw new CliException("Missing or invalid --content-id.");

        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        await client.RequestPresenceAsync(contentId, cancellation.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> PingAsync(CliOptions options)
    {
        var endpoint = CliEndpoint.FromOptions(options);
        var statePath = options.Get("state", DeviceIdentity.DefaultPath);
        var identity = DeviceIdentity.LoadExisting(statePath);
        using var cancellation = CreateCancellation();

        await using var client = CreateClient(endpoint, identity, statePath, options);
        await client.AuthenticateAsync(endpoint.Code, cancellation.Token).ConfigureAwait(false);
        await client.PingAsync(cancellation.Token).ConfigureAwait(false);
        return 0;
    }

    private static FreeChatClient CreateClient(
        CliEndpoint endpoint,
        DeviceIdentity identity,
        string statePath,
        CliOptions options) =>
        new(endpoint, identity, statePath, options.Has("json"), options.Has("print-json"));

    private static SendRequest BuildSendRequest(CliOptions options)
    {
        var raw = options.Get("raw");
        if (raw is not null)
            return new SendRequest { RawText = raw };

        var text = options.Get("text")
                   ?? throw new CliException("Missing --text or --raw.");
        var channel = ParseChannel(options.Get("channel", "say"));
        if (channel == ChatChannel.Tell)
        {
            var target = options.Require("target");
            var at = target.LastIndexOf('@');
            if (at <= 0 || at == target.Length - 1)
                throw new CliException("--target must be Name@World for tells.");

            return new SendRequest
            {
                Chat = new ChatSend
                {
                    Channel = channel,
                    Text = text,
                    Target = new CharacterRef
                    {
                        Name = target[..at],
                        WorldName = target[(at + 1)..],
                    },
                },
            };
        }

        return new SendRequest
        {
            Chat = new ChatSend
            {
                Channel = channel,
                Text = text,
            },
        };
    }

    private static ChatChannel ParseChannel(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized switch
        {
            "say" => ChatChannel.Say,
            "party" or "p" => ChatChannel.Party,
            "shout" => ChatChannel.Shout,
            "yell" => ChatChannel.Yell,
            "alliance" => ChatChannel.Alliance,
            "fc" or "freecompany" => ChatChannel.FreeCompany,
            "ls1" or "linkshell1" => ChatChannel.Linkshell1,
            "ls2" or "linkshell2" => ChatChannel.Linkshell2,
            "ls3" or "linkshell3" => ChatChannel.Linkshell3,
            "ls4" or "linkshell4" => ChatChannel.Linkshell4,
            "ls5" or "linkshell5" => ChatChannel.Linkshell5,
            "ls6" or "linkshell6" => ChatChannel.Linkshell6,
            "ls7" or "linkshell7" => ChatChannel.Linkshell7,
            "ls8" or "linkshell8" => ChatChannel.Linkshell8,
            "cwls1" => ChatChannel.CrossLinkshell1,
            "cwls2" => ChatChannel.CrossLinkshell2,
            "cwls3" => ChatChannel.CrossLinkshell3,
            "cwls4" => ChatChannel.CrossLinkshell4,
            "cwls5" => ChatChannel.CrossLinkshell5,
            "cwls6" => ChatChannel.CrossLinkshell6,
            "cwls7" => ChatChannel.CrossLinkshell7,
            "cwls8" => ChatChannel.CrossLinkshell8,
            "novice" or "n" => ChatChannel.NoviceNetwork,
            "pvp" or "pvpteam" => ChatChannel.PvpTeam,
            "tell" or "t" => ChatChannel.Tell,
            _ => throw new CliException($"Unknown channel: {value}"),
        };
    }

    private static int Decode(CliOptions options)
    {
        var input = options.Get("input");
        var base64 = options.Get("base64");
        var hex = options.Get("hex");
        if ((input is null ? 0 : 1) + (base64 is null ? 0 : 1) + (hex is null ? 0 : 1) != 1)
            throw new CliException("Use exactly one of --input, --base64, or --hex.");

        byte[] bytes;
        if (base64 is not null)
        {
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new CliException("--base64 is not valid base64.");
            }
        }
        else if (hex is not null)
        {
            try
            {
                bytes = Convert.FromHexString(hex.Replace(":", string.Empty).Replace(" ", string.Empty));
            }
            catch (FormatException)
            {
                throw new CliException("--hex is not valid hex.");
            }
        }
        else if (input == "-")
        {
            using var stdin = Console.OpenStandardInput();
            using var memory = new MemoryStream();
            stdin.CopyTo(memory);
            bytes = memory.ToArray();
        }
        else
        {
            bytes = File.ReadAllBytes(input!);
        }

        var encoding = options.Get("encoding", "protobuf").ToLowerInvariant();
        var envelope = encoding switch
        {
            "protobuf" or "pb" or "binary" => EnvelopeCodec.Decode(bytes, json: false),
            "json" => EnvelopeCodec.Decode(bytes, json: true),
            _ => throw new CliException("--encoding must be protobuf or json."),
        };
        Console.WriteLine(JsonFormatter.Default.Format(envelope));
        return 0;
    }

    private static CancellationTokenSource CreateCancellation()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            freechat 0.2.0 - FreeChat v1 reference client

            Usage:
              freechat pair      --uri <freechat://...> [--state <path>] [--json]
              freechat pair      --host <host> --port <port> --fingerprint <hex> --code <code>
              freechat subscribe --uri <freechat://...> [options]
              freechat send      --uri <freechat://...> (--raw <text> | --channel <name> --text <text>)
              freechat friends   --uri <freechat://...> [--page-size <n>]
              freechat presence  --uri <freechat://...> --content-id <id>
              freechat ping      --uri <freechat://...>
              freechat decode    (--input <file|-> | --base64 <data> | --hex <data>) [--encoding json|protobuf]

            Common options:
              --host <host>          Server host (default 127.0.0.1)
              --port <port>          Server port (default 14777)
              --fingerprint <hex>    SHA-256 of the server P-256 public key (required)
              --uri <link>           freechat://pair?... deep link (fills host/port/fp/sid/code)
              --state <path>         Device state file (default: application data/FreeChat/cli-state.json)
              --json                 Use freechat.v1.json transport (server debug mode)
              --print-json           Print received envelopes as proto3 JSON

            Subscribe options:
              --sources <list>       post (default), pre, structured, all; comma-separated
              --keyword <text>       Substring filter
              --regex <pattern>      Non-backtracking regex filter
              --exclude-own          Drop outgoing events
              --include-battle       Include battle events
              --from <sequence>      Replay after this sequence
              --count <n>            Exit after n chat events (0 = forever)

            Send options:
              --raw <text>           Raw text or slash command (max 500 UTF-8 bytes)
              --channel <name>       say, party, shout, yell, alliance, fc, ls1..8, cwls1..8, novice, pvp, tell
              --text <text>          Structured message text
              --target <Name@World>  Required for --channel tell
              --wait-echo            Wait up to 3 seconds for a best-effort ECHOED result

            Social options:
              --page-size <n>        Friend list page size (0 = server default, max 200)
              --owner-key <key>      Expected owner key; reject if the character changed
              --owner-epoch <id>     Expected session id; reject if the session changed
              --content-id <id>      Friend ContentId for the presence query
            """);
    }
}
