using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Illusion.Mcp.Services;
using Illusion.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Illusion.Mcp;

/// <summary>
/// The embedded MCP server: a Kestrel endpoint that lives for as long as the application does,
/// serving tools to MCP clients over streamable HTTP.
/// <para>
/// <b>No authorization by design.</b> Nothing calls <c>AddAuthentication</c> or
/// <c>RequireAuthorization</c>, so every request that reaches the server is served — that is simply
/// the unconfigured state, not something switched off. Three things keep that safe, and a future
/// tool that touches the user's files depends on all three:
/// </para>
/// <list type="number">
/// <item>The socket is bound to the loopback address, so nothing off this machine can connect.</item>
/// <item>Host filtering refuses requests arriving under a foreign host name. This is what stops
/// DNS rebinding, where a page resolves its own domain to 127.0.0.1 so the browser treats us as
/// same-origin. Asserted by <c>probe-mcp</c>, because the wiring is not visible from this file.</item>
/// <item><b>CORS is deliberately never configured, and that is a security boundary — not an
/// omission.</b> Host filtering does nothing against the simpler attack: a page can post straight
/// to 127.0.0.1 with an honest Host header. What blocks that is the browser's own rule that a
/// cross-origin POST may only carry a form or plain-text content type without asking permission
/// first — and this transport reads nothing but <c>application/json</c>. Sending JSON needs a
/// preflight, and with no CORS policy there is no answer to it, so the request is never sent.
/// Add <c>AddCors</c> for some future browser-facing surface and that protection disappears.</item>
/// </list>
/// <para>
/// Starting never throws. A busy port — the normal outcome of launching a second copy of the
/// application — leaves the host <see cref="McpServerStatus.Failed"/> with a readable reason for the
/// launcher to display, and everything else carries on working.
/// </para>
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly McpHostOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private McpServerState _state = McpServerState.Stopped;

    /// <summary>Guarded by <see cref="_lifecycle"/>. Latches on the first stop and never clears.</summary>
    private bool _stopRequested;

    public McpServerHost(McpHostOptions options) => _options = options;

    /// <summary>The current snapshot. Safe to read from any thread.</summary>
    public McpServerState State => Volatile.Read(ref _state);

    /// <summary>
    /// Raised whenever <see cref="State"/> changes. <b>Fires on a thread-pool thread</b> — a UI
    /// subscriber has to marshal to its dispatcher before touching controls.
    /// </summary>
    public event Action<McpServerState>? StateChanged;

    /// <summary>
    /// Binds the port and begins serving. Returns once the server is listening (or has failed);
    /// it does not block for the lifetime of the server, and it never throws.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => StartCoreAsync(cancellationToken), CancellationToken.None);

    /// <summary>
    /// Stops serving, letting in-flight requests finish within <paramref name="timeout"/>.
    /// Like <see cref="StartAsync"/>, it never throws.
    /// </summary>
    public Task StopAsync(TimeSpan timeout) => Task.Run(() => StopCoreAsync(timeout));

    // Both entry points hand their work to the thread pool, and that is load-bearing rather than
    // tidiness. The hosting stack captures whatever SynchronizationContext is current when it
    // suspends, and WPF runs Application.OnExit *after* the dispatcher loop has ended: a stop
    // started on the UI thread posts its continuation to a dispatcher that will never run another
    // work item, so the caller waits forever. Measured: stopping from the UI thread hung
    // indefinitely, stopping from the pool took 18 ms. Running the start off-thread too keeps the
    // dispatcher out of the picture entirely, and keeps Kestrel's startup off the UI thread.

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Held until this server is adopted into _app. Anything still in here at the end was built
        // but never taken into service — most often because binding the port threw — and would
        // otherwise leak its DI container and any socket Kestrel had already opened.
        WebApplication? pending = null;
        try
        {
            // The host is one-shot on purpose. Start and stop are two independent thread-pool work
            // items, and the semaphore only orders them once both are running — it cannot say which
            // was *asked for* first. Close the application quickly enough after launch and the stop
            // can win, find nothing to stop, and return, leaving the start to bind the port during
            // process teardown. The flag makes a stop that has already been requested win for good.
            if (_app is not null || _stopRequested)
            {
                return;
            }

            Publish(new McpServerState(McpServerStatus.Starting, null, null));

            pending = Build();
            await pending.StartAsync(cancellationToken).ConfigureAwait(false);

            // Where it ACTUALLY listens, not where it was told to. Asking for a loopback address is
            // not the same as getting one, and an unauthenticated endpoint that quietly ends up on
            // a routable interface is the worst failure this class has. Refusing beats serving.
            string? exposed = pending.Urls.FirstOrDefault(url => !IsLoopback(url));
            if (exposed is not null)
            {
                Publish(new McpServerState(McpServerStatus.Failed, null,
                    $"refused to serve on {exposed} - this endpoint has no authentication and must stay on the loopback interface"));
                return;
            }

            _app = pending;
            pending = null;
            Publish(new McpServerState(McpServerStatus.Running, ResolveAddress(_app), null));
        }
        catch (Exception ex)
        {
            // The toolkit is usable without the server; a failure here is reported, never fatal.
            _app = null;
            Publish(new McpServerState(McpServerStatus.Failed, null, Describe(ex)));
        }
        finally
        {
            if (pending is not null)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }

            _lifecycle.Release();
        }
    }

    /// <summary>True only for an address on this machine's loopback interface.</summary>
    private static bool IsLoopback(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && IPAddress.TryParse(uri.Host, out IPAddress? ip)
            && IPAddress.IsLoopback(ip);

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Set before the early return, so a start still queued behind us stands down (see
            // StartCoreAsync). Once stopped, this host stays stopped.
            _stopRequested = true;

            if (_app is null)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(timeout);
            try
            {
                await _app.StopAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
                Publish(McpServerState.Stopped);
            }
        }
        catch (Exception)
        {
            // Shutdown is best-effort: the process is on its way out either way.
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Without this the content root follows the process working directory, which for a
            // desktop app is wherever it happened to be launched from — and a stray appsettings.json
            // sitting in that folder would then be picked up as our configuration.
            ContentRootPath = AppContext.BaseDirectory,
        });


        // A windowed process has no console attached, so the default console logger writes into
        // nothing. Dropping the providers keeps that pointless work out of every request.
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            // The framework points Kestrel's endpoint loader at the "Kestrel" configuration section,
            // and endpoints found there OUTRANK any address the application asks for. That made a
            // single `Kestrel__Endpoints__X__Url` environment variable enough to move this
            // unauthenticated server onto 0.0.0.0 — reachable from the whole network — while the
            // launcher went on displaying 127.0.0.1. Verified by experiment, then closed by handing
            // the loader an empty configuration: outside settings have nothing left to say here.
            options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);

            // Stated in code, as an explicit endpoint rather than a URL string. Explicit endpoints
            // also outrank the hosting URLs that ASPNETCORE_URLS would otherwise supply.
            options.Listen(IPAddress.Loopback, _options.Port);
        });

        // Only requests addressed to the loopback host are answered. Bind address alone does not
        // cover this: a browser can be made to resolve an attacker's domain to 127.0.0.1, and the
        // request would then arrive here carrying that domain in its Host header.
        builder.Services.Configure<HostFilteringOptions>(o =>
            o.AllowedHosts = ["127.0.0.1", "localhost"]);

        // The archive cache the browsing tools share. A singleton in the server's own container
        // rather than something the application registers: it holds no application state, only
        // decompressed file bytes, and its lifetime is the server's.
        builder.Services.AddSingleton<ArchiveService>();

        // Stateless: no session affinity, every request stands alone. That suits a local tool
        // server, and it is set explicitly because the SDK's own samples warn that the default
        // may flip in a future release. Note this says nothing about the cache above — statelessness
        // is a property of the transport, while the cache is keyed by file path and shared by all
        // callers, so it survives across requests exactly as intended.
        // Registered class by class rather than with WithToolsFromAssembly, so that what this server
        // serves is a list somebody wrote down: a tool type added to the project is not silently
        // published to every client until it is named here.
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<PingTool>()
            .WithTools<SdsTools>()
            .WithTools<UtilityTools>()
            .WithTools<TableTools>()
            .WithTools<StreamMapTools>()
            .WithTools<DecodeTools>()
            .WithTools<ResourceDecodeTools>()
            .WithTools<EffectsTools>()
            .WithTools<MaterialTools>()
            .WithTools<TextureTools>()
            .WithTools<FormatTools>()
            .WithTools<LuaTools>()
            .WithTools<EditorTools>()
            .WithTools<CarTools>();

        _options.ConfigureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.MapMcp(McpHostOptions.Path);
        return app;
    }

    /// <summary>
    /// The client-facing URL. With <see cref="McpHostOptions.Port"/> at 0 the real port is only known
    /// once the socket is bound, so it is read back from the server rather than from the options.
    /// </summary>
    private string ResolveAddress(WebApplication app)
    {
        string? bound = app.Urls.FirstOrDefault();
        if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out Uri? uri))
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"http://127.0.0.1:{uri.Port}{McpHostOptions.Path}");
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"http://127.0.0.1:{_options.Port}{McpHostOptions.Path}");
    }

    /// <summary>
    /// Reduces a startup failure to one line the launcher can show.
    /// <para>
    /// None of it quotes <c>Exception.Message</c>. Those come from the OS in the system's language —
    /// only the process's formatting culture is pinned at startup, not its UI culture — so a bind
    /// refused by a reserved port range reads as Russian text on a Russian Windows, and this
    /// application's UI is English throughout. Socket error names and type names are identifiers,
    /// so they say what happened without that risk.
    /// </para>
    /// </summary>
    private string Describe(Exception ex)
    {
        SocketError? socket = FindSocketError(ex);

        // The one failure users actually hit, and worth naming outright: its own message never
        // mentions the likely cause.
        if (socket == SocketError.AddressAlreadyInUse)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"port {_options.Port} is already in use - another Illusion Toolkit is probably running");
        }

        return socket is not null
            ? string.Create(CultureInfo.InvariantCulture, $"port {_options.Port} could not be bound ({socket})")
            : string.Create(CultureInfo.InvariantCulture, $"port {_options.Port} failed to open ({ex.GetType().Name})");
    }

    private static SocketError? FindSocketError(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SocketException socket)
            {
                return socket.SocketErrorCode;
            }
        }

        return null;
    }

    private void Publish(McpServerState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(state);
    }
}
