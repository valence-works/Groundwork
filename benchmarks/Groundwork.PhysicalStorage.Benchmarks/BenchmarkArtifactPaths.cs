namespace Groundwork.PhysicalStorage.Benchmarks;

/// <summary>
/// Resolves benchmark-evidence paths without allowing either lexical traversal or a
/// reparse-point hop outside the declared artifact root. Evidence is intentionally
/// local and re-sealable, but it must at least describe files beneath the run that
/// claims them.
/// </summary>
internal static class BenchmarkArtifactPaths
{
    public static string ResolveExisting(string root, string relative, string description) =>
        Resolve(root, relative, description, requireFile: true, prepareForWrite: false);

    public static string ResolveForWrite(string root, string relative, string description) =>
        Resolve(root, relative, description, requireFile: false, prepareForWrite: true);

    public static string ResolveAbsoluteExisting(string root, string path, string description) =>
        ResolveExisting(root, Relative(root, path, description), description);

    public static string ResolveAbsoluteForWrite(string root, string path, string description) =>
        ResolveForWrite(root, Relative(root, path, description), description);

    public static string RequireExistingFile(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(canonicalPath)
                     ?? throw new InvalidOperationException($"{description} has no parent directory.");
        return ResolveExisting(parent, Path.GetFileName(canonicalPath), description);
    }

    public static string PrepareStandaloneForWrite(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(canonicalPath)
                     ?? throw new InvalidOperationException($"{description} has no parent directory.");
        return ResolveForWrite(parent, Path.GetFileName(canonicalPath), description);
    }

    public static string Relative(string root, string path, string description)
    {
        var canonicalRoot = RequireRoot(root, create: false);
        var canonicalPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        if (relative == "." || IsTraversal(relative))
            throw new InvalidOperationException($"{description} escapes the artifact root.");
        return CanonicalRelative(relative, description);
    }

    public static string RequireCanonicalRelative(string relative, string description)
    {
        var canonical = CanonicalRelative(relative, description)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(relative, canonical, StringComparison.Ordinal))
            throw new InvalidOperationException($"{description} must use its canonical relative path.");
        return canonical;
    }

    public static string RequireRoot(string root, bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var canonicalRoot = Path.GetFullPath(root);
        if (create)
            Directory.CreateDirectory(canonicalRoot);
        if (!Directory.Exists(canonicalRoot))
            throw new DirectoryNotFoundException($"Artifact root '{canonicalRoot}' was not found.");
        RejectReparsePoint(canonicalRoot, "Artifact root");
        return canonicalRoot;
    }

    private static string Resolve(
        string root,
        string relative,
        string description,
        bool requireFile,
        bool prepareForWrite)
    {
        var canonicalRoot = RequireRoot(root, create: prepareForWrite);
        var canonicalRelative = CanonicalRelative(relative, description);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, canonicalRelative));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{description} escapes the artifact root.");

        var relativeSegments = canonicalRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = canonicalRoot;
        for (var index = 0; index < relativeSegments.Length; index++)
        {
            current = Path.Combine(current, relativeSegments[index]);
            if (!ExistsOrIsLink(current))
                break;
            RejectReparsePoint(current, description);
        }

        if (prepareForWrite)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Directory.CreateDirectory can traverse an existing symlink. Recheck every
        // component after preparing the destination so a write never follows one.
        RejectExistingComponents(canonicalRoot, relativeSegments, description);
        if (requireFile && !File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"{description} was not found.", path);
        return path;
    }

    private static string CanonicalRelative(string relative, string description)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException($"{description} paths must be non-empty and relative.");

        var normalized = relative
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidOperationException($"{description} contains a non-canonical relative path.");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static bool IsTraversal(string relative) =>
        relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static void RejectExistingComponents(string root, IEnumerable<string> segments, string description)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!ExistsOrIsLink(current))
                break;
            RejectReparsePoint(current, description);
        }
    }

    private static bool ExistsOrIsLink(string path) =>
        File.Exists(path) ||
        Directory.Exists(path) ||
        LinkTarget(path) is not null;

    private static void RejectReparsePoint(string path, string description)
    {
        if (LinkTarget(path) is not null ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} traverses a symbolic link or reparse point.");
        }
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
