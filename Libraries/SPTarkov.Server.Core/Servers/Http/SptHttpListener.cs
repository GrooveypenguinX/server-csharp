using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Servers.Http;

[Injectable]
public class SptHttpListener(
    HttpRouter httpRouter,
    IEnumerable<ISerializer> serializers,
    ISptLogger<SptHttpListener> logger,
    ISptLogger<RequestLogger> requestsLogger,
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    LocalisationService localisationService
    ) : IHttpListener
{
    // We want to read 1KB at a time, for most request this is already big enough
    private const int BodyReadBufferSize = 1024 * 1;

    private static readonly ImmutableHashSet<string> SupportedMethods = ["GET", "PUT", "POST"];


    protected readonly HttpRouter _router = httpRouter;
    protected readonly IEnumerable<ISerializer> _serializers = serializers;

    public bool CanHandle(string _, HttpRequest req)
    {
        return SupportedMethods.Contains(req.Method);
    }

    public async Task Handle(string sessionId, HttpRequest req, HttpResponse resp)
    {
        switch (req.Method)
        {
            case "GET":
                {
                    var response = GetResponse(sessionId, req, null);
                    await SendResponse(sessionId, req, resp, null, response);
                    break;
                }
            // these are handled almost identically.
            case "POST":
            case "PUT":
                {
                    // Contrary to reasonable expectations, the content-encoding is _not_ actually used to
                    // determine if the payload is compressed. All PUT requests are, and POST requests without
                    // debug = 1 are as well. This should be fixed.
                    // let compressed = req.headers["content-encoding"] === "deflate";
                    var requestIsCompressed = !req.Headers.TryGetValue("requestcompressed", out var compressHeader) ||
                      compressHeader != "0";
                    var requestCompressed = req.Method == "PUT" || requestIsCompressed;

                    var body = string.Empty;
                    using MemoryStream bufferStream = new();

                    var buffer = new byte[BodyReadBufferSize];
                    int bytesRead;

                    while ((bytesRead = await req.Body.ReadAsync(buffer)) > 0)
                    {
                        await bufferStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    }

                    bufferStream.Position = 0;

                    if (requestCompressed)
                    {
                        await using var deflateStream = new ZLibStream(bufferStream, CompressionMode.Decompress);
                        await using var decompressedStream = new MemoryStream();
                        await deflateStream.CopyToAsync(decompressedStream);
                        decompressedStream.Position = 0;

                        using var reader = new StreamReader(decompressedStream, Encoding.UTF8);
                        body = await reader.ReadToEndAsync();
                    }
                    else
                    {
                        // No decompression needed, decode directly from the bufferStream's buffer
                        bufferStream.Position = 0;
                        using var reader = new StreamReader(bufferStream, Encoding.UTF8);
                        body = await reader.ReadToEndAsync();
                    }

                    if (!requestIsCompressed)
                    {
                        if (logger.IsLogEnabled(LogLevel.Debug))
                        {
                            logger.Debug(body);
                        }
                    }

                    var response = GetResponse(sessionId, req, body);
                    await SendResponse(sessionId, req, resp, body, response);
                    break;
                }

            default:
                {
                    logger.Warning($"{localisationService.GetText("unknown_request")}: {req.Method}");
                    break;
                }
        }
    }

    /// <summary>
    ///     Send HTTP response back to sender
    /// </summary>
    /// <param name="sessionID"> Player id making request </param>
    /// <param name="req"> Incoming request </param>
    /// <param name="resp"> Outgoing response </param>
    /// <param name="body"> Buffer </param>
    /// <param name="output"> Server generated response data</param>
    public async Task SendResponse(
        string sessionID,
        HttpRequest req,
        HttpResponse resp,
        object? body,
        string output
    )
    {
        if (body == null)
        {
            body = new object();
        }

        var bodyInfo = jsonUtil.Serialize(body);

        if (IsDebugRequest(req))
        {
            // Send only raw response without transformation
            await SendJson(resp, output, sessionID);
            if (logger.IsLogEnabled(LogLevel.Debug))
            {
                logger.Debug($"Response: {output}");
            }

            LogRequest(req, output);
            return;
        }

        // Not debug, minority of requests need a serializer to do the job (IMAGE/BUNDLE/NOTIFY)
        var serialiser = _serializers.FirstOrDefault(x => x.CanHandle(output));
        if (serialiser != null)
        {
            await serialiser.Serialize(sessionID, req, resp, bodyInfo);
        }
        else
            // No serializer can handle the request (majority of requests dont), zlib the output and send response back
        {
            await SendZlibJson(resp, output, sessionID);
        }

        LogRequest(req, output);
    }

    /// <summary>
    ///     Is request flagged as debug enabled
    /// </summary>
    /// <param name="req"> Incoming request </param>
    /// <returns> True if request is flagged as debug </returns>
    protected bool IsDebugRequest(HttpRequest req)
    {
        return req.Headers.TryGetValue("responsecompressed", out var value) && value == "0";
    }

    /// <summary>
    ///     Log request if enabled
    /// </summary>
    /// <param name="req"> Log request if enabled </param>
    /// <param name="output"> Output string </param>
    protected void LogRequest(HttpRequest req, string output)
    {
        if (ProgramStatics.ENTRY_TYPE() != EntryType.RELEASE)
        {
            var log = new Response(req.Method, output);
            requestsLogger.Info($"RESPONSE={jsonUtil.Serialize(log)}");
        }
    }

    public string GetResponse(string sessionID, HttpRequest req, string? body)
    {
        var output = _router.GetResponse(req, sessionID, body, out var deserializedObject);
        /* route doesn't exist or response is not properly set up */
        if (string.IsNullOrEmpty(output))
        {
            logger.Error(localisationService.GetText("unhandled_response", req.Path.ToString()));
            logger.Info(jsonUtil.Serialize(deserializedObject));
            output = httpResponseUtil.GetBody<object?>(null, BackendErrorCodes.HTTPNotFound, $"UNHANDLED RESPONSE: {req.Path.ToString()}");
        }

        if (ProgramStatics.ENTRY_TYPE() != EntryType.RELEASE)
        {
            // Parse quest info into object
            var log = new Request(req.Method, new RequestData(req.Path, req.Headers, deserializedObject));
            requestsLogger.Info($"REQUEST={jsonUtil.Serialize(log)}");
        }

        return output;
    }

    public async Task SendJson(HttpResponse resp, string? output, string sessionID)
    {
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        resp.Headers.Append("Set-Cookie", $"PHPSESSID={sessionID}");
        if (!string.IsNullOrEmpty(output))
        {
            await resp.Body.WriteAsync(Encoding.UTF8.GetBytes(output));
        }

        await resp.StartAsync();
        await resp.CompleteAsync();
    }

    public async Task SendZlibJson(HttpResponse resp, string? output, string sessionID)
    {
        using (var ms = new MemoryStream())
        {
            using (var deflateStream = new ZLibStream(ms, CompressionLevel.SmallestSize))
            {
                await deflateStream.WriteAsync(Encoding.UTF8.GetBytes(output));
            }

            var bytes = ms.ToArray();
            await resp.Body.WriteAsync(bytes);
        }

        await resp.StartAsync();
        await resp.CompleteAsync();
    }

    private record Response(string Method, string jsonData);

    private record Request(string Method, object output);

    private record RequestData(string Url, object Headers, object Data);
}
