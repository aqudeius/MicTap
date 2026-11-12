using System.Reflection;
using System.Runtime.Loader;

namespace MicTap;

static class PortableLayout
{
    public static string? SystemDirectory { get; } = FindSystemDirectory();
    public static string DataDirectory => SystemDirectory ?? AppContext.BaseDirectory;

    static string? FindSystemDirectory()
    {
        string? executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (executableDirectory == null) return null;

        string systemDirectory = Path.Combine(executableDirectory, "System");
        string dllDirectory = Path.Combine(systemDirectory, "DLLs");
        return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), dllDirectory,
            StringComparison.OrdinalIgnoreCase) ? systemDirectory : null;
    }

    public static void RegisterLocalizations()
    {
        if (SystemDirectory != null) AssemblyLoadContext.Default.Resolving += ResolveLocalization;
    }

    static Assembly? ResolveLocalization(AssemblyLoadContext context, AssemblyName name)
    {
        string? assembly = name.Name;
        string? culture = name.CultureName;
        if (assembly == null || !assembly.EndsWith(".resources", StringComparison.Ordinal) ||
            Path.GetFileName(assembly) != assembly || string.IsNullOrEmpty(culture) ||
            culture is "." or ".." || Path.GetFileName(culture) != culture)
            return null;

        string path = Path.Combine(SystemDirectory!, "Localizations", culture, assembly + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
