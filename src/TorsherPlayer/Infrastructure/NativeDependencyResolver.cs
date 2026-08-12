using System.Reflection;
using System.Runtime.InteropServices;

namespace TorsherPlayer.Infrastructure;

internal static class NativeDependencyResolver
{
    internal const string MpvLibrary = "torsherplayer_mpv";
    internal const string SdlLibrary = "torsherplayer_sdl3";

    private static int _registered;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IntPtr> Loaded = new(StringComparer.Ordinal);

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeDependencyResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is not (MpvLibrary or SdlLibrary)) return IntPtr.Zero;

        lock (Gate)
        {
            if (Loaded.TryGetValue(libraryName, out var handle) && handle != IntPtr.Zero)
                return handle;

            var names = libraryName == MpvLibrary ? MpvNames() : SdlNames();
            foreach (var directory in CandidateDirectories())
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory, name);
                    if (!File.Exists(candidate)) continue;
                    if (NativeLibrary.TryLoad(candidate, out handle))
                    {
                        Loaded[libraryName] = handle;
                        return handle;
                    }
                }
            }

            // Fall back to the platform loader. This makes distro/Homebrew-installed
            // libraries work without copying them next to the executable.
            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, assembly, searchPath, out handle))
                {
                    Loaded[libraryName] = handle;
                    return handle;
                }
            }

            var dependency = libraryName == MpvLibrary ? "libmpv" : "SDL3";
            var expected = string.Join(", ", names);
            throw new DllNotFoundException(
                $"Не найдена {dependency}. Ожидался один из файлов: {expected}. " +
                "Установите системную библиотеку или используйте официальный release-пакет Torsher Player.");
        }
    }

    private static string[] MpvNames()
    {
        if (OperatingSystem.IsWindows()) return ["mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv.dll"];
        if (OperatingSystem.IsMacOS()) return ["libmpv.2.dylib", "libmpv.dylib"];
        return ["libmpv.so.2", "libmpv.so"];
    }

    private static string[] SdlNames()
    {
        if (OperatingSystem.IsWindows()) return ["SDL3.dll"];
        if (OperatingSystem.IsMacOS()) return ["libSDL3.0.dylib", "libSDL3.dylib"];
        return ["libSDL3.so.0", "libSDL3.so"];
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        foreach (var dir in RawDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string normalized;
            try { normalized = Path.GetFullPath(dir); }
            catch { continue; }
            if (seen.Add(normalized)) yield return normalized;
        }
    }

    private static IEnumerable<string> RawDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir;

        var rid = CurrentPortableRid();
        if (rid is not null)
        {
            yield return Path.Combine(baseDir, "native", rid);
            yield return Path.Combine(baseDir, "runtimes", rid, "native");
        }

        if (Environment.GetEnvironmentVariable("TORSHER_PLAYER_NATIVE_PATH") is { Length: > 0 } customSearch)
        {
            foreach (var dir in customSearch.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return dir;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Homebrew uses /opt/homebrew on Apple Silicon and /usr/local on Intel.
            yield return "/opt/homebrew/lib";
            yield return "/usr/local/lib";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/local/lib";
            yield return "/usr/lib";
            yield return "/lib";
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                yield return "/usr/lib/x86_64-linux-gnu";
                yield return "/lib/x86_64-linux-gnu";
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                yield return "/usr/lib/aarch64-linux-gnu";
                yield return "/lib/aarch64-linux-gnu";
            }
        }

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearch)
        {
            foreach (var dir in nativeSearch.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return dir;
        }
    }

    private static string? CurrentPortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        if (os is null) return null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null
        };
        return arch is null ? null : $"{os}-{arch}";
    }
}
