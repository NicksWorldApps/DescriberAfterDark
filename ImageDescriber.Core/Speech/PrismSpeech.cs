using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImageDescriber.Core.Speech;

public interface ISpeechOutput : IDisposable
{
    /// <summary>Name of the screen reader or voice in use, for example "NVDA", "JAWS", or "SAPI".</summary>
    string Name { get; }

    /// <summary>Speaks text (and sends it to braille where supported).</summary>
    void Say(string text, bool interrupt = true);

    void Stop();
}

/// <summary>Used when Prism isn't available. The text still appears in the window for the screen reader.</summary>
public sealed class SilentSpeech : ISpeechOutput
{
    public string Name => "None";
    public void Say(string text, bool interrupt = true) { }
    public void Stop() { }
    public void Dispose() { }
}

/// <summary>
/// Speech through Prism (https://github.com/ethindp/prism), which routes text to
/// NVDA, JAWS, Narrator and other screen readers, or to SAPI if none is running.
///
/// All calls must come from the same thread (the UI thread in this app).
///
/// This picks the backend itself instead of calling prism_registry_acquire_best,
/// to avoid a bug in Prism 0.16.2 to 0.16.4 where NVDA was chosen even when it
/// wasn't running, leaving JAWS users with no speech.
/// </summary>
public sealed class PrismSpeech : ISpeechOutput
{
    private IntPtr _context;
    private IntPtr _backend;

    public string Name { get; }

    private PrismSpeech(IntPtr context, IntPtr backend, string name)
    {
        _context = context;
        _backend = backend;
        Name = name;
    }

    /// <summary>Connects to the running screen reader, or falls back to silent output.</summary>
    public static ISpeechOutput CreateBest(Action<string>? log = null)
    {
        try
        {
            var ctx = PrismNative.prism_init(IntPtr.Zero);
            if (ctx == IntPtr.Zero)
            {
                log?.Invoke("Prism failed to start.");
                return new SilentSpeech();
            }

            foreach (var (id, name) in GetCandidates(ctx))
            {
                if (!IsPlausiblyRunning(name)) continue;

                var backend = PrismNative.prism_registry_create(ctx, id);
                if (backend == IntPtr.Zero) continue;

                var err = PrismNative.prism_backend_initialize(backend);
                if (err == PrismNative.PRISM_OK)
                {
                    log?.Invoke($"Speech output: {name}");
                    return new PrismSpeech(ctx, backend, name);
                }

                log?.Invoke($"{name} not available: {PrismNative.ErrorText(err)}");
                PrismNative.prism_backend_free(backend);
            }

            PrismNative.prism_shutdown(ctx);
            log?.Invoke("No speech backend available.");
        }
        catch (DllNotFoundException)
        {
            log?.Invoke("prism.dll was not found next to the program.");
        }
        catch (EntryPointNotFoundException ex)
        {
            log?.Invoke($"prism.dll is an incompatible version: {ex.Message}");
        }
        catch (BadImageFormatException)
        {
            log?.Invoke("prism.dll is built for a different processor type (needs 64-bit).");
        }
        return new SilentSpeech();
    }

    /// <summary>All registered backends, highest priority first (screen readers before plain voices).</summary>
    private static List<(ulong Id, string Name)> GetCandidates(IntPtr ctx)
    {
        var list = new List<(ulong Id, string Name, int Priority)>();
        var count = (long)(ulong)PrismNative.prism_registry_count(ctx);
        for (long i = 0; i < count; i++)
        {
            var id = PrismNative.prism_registry_id_at(ctx, (nuint)(ulong)i);
            if (id == 0) continue; // PRISM_BACKEND_INVALID
            var name = Marshal.PtrToStringUTF8(PrismNative.prism_registry_name(ctx, id)) ?? $"Backend {id}";
            list.Add((id, name, PrismNative.prism_registry_priority(ctx, id)));
        }
        return list.OrderByDescending(b => b.Priority).Select(b => (b.Id, b.Name)).ToList();
    }

    /// <summary>
    /// Extra check for screen readers whose Prism backend may report success when not running.
    /// Unknown backends are assumed fine and left to Prism's own initialization.
    /// </summary>
    private static bool IsPlausiblyRunning(string backendName)
    {
        return backendName switch
        {
            "NVDA" => ProcessRunning("nvda"),
            "JAWS" => ProcessRunning("jfw"),
            _ => true,
        };
    }

    private static bool ProcessRunning(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }
        catch
        {
            return true; // If we can't tell, let Prism decide.
        }
    }

    public void Say(string text, bool interrupt = true)
    {
        if (_backend == IntPtr.Zero || string.IsNullOrWhiteSpace(text)) return;

        // "output" sends to both speech and braille where the screen reader supports it.
        var err = PrismNative.prism_backend_output(_backend, text, interrupt);
        if (err != PrismNative.PRISM_OK)
            PrismNative.prism_backend_speak(_backend, text, interrupt);
    }

    public void Stop()
    {
        if (_backend != IntPtr.Zero) PrismNative.prism_backend_stop(_backend);
    }

    public void Dispose()
    {
        if (_backend != IntPtr.Zero)
        {
            PrismNative.prism_backend_free(_backend);
            _backend = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            PrismNative.prism_shutdown(_context);
            _context = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Raw bindings to Prism's C API. Signatures follow Prism's documentation
/// (context-management, registry-functions, backend-functions).
/// prism.dll (64-bit) must sit next to the program.
/// </summary>
internal static class PrismNative
{
    private const string Lib = "prism";

    /// <summary>PRISM_OK. PrismError is a C enum; success is the zero value.</summary>
    public const int PRISM_OK = 0;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr prism_init(IntPtr config); // config may be NULL for defaults

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void prism_shutdown(IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint prism_registry_count(IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong prism_registry_id_at(IntPtr context, nuint index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr prism_registry_name(IntPtr context, ulong id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int prism_registry_priority(IntPtr context, ulong id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr prism_registry_create(IntPtr context, ulong id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int prism_backend_initialize(IntPtr backend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int prism_backend_output(
        IntPtr backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.U1)] bool interrupt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int prism_backend_speak(
        IntPtr backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.U1)] bool interrupt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int prism_backend_stop(IntPtr backend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void prism_backend_free(IntPtr backend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr prism_error_string(int error);

    public static string ErrorText(int error)
    {
        try
        {
            return Marshal.PtrToStringUTF8(prism_error_string(error)) ?? $"error {error}";
        }
        catch (EntryPointNotFoundException)
        {
            return $"error {error}";
        }
    }
}
