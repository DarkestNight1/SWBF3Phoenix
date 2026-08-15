using System;
using System.IO;
using UnityEngine;

/// <summary>
/// PhxPath will:<br/>
/// - always use forward slashes<br/>
/// - ensure there is NO trailing slash at the end<br/>
/// - ensure there are no double slashes<br/>
/// - might or might not start with a slash
/// </summary>
public class PhxPath
{
    string P;
    
    public PhxPath(string path)
    {
        P = path;

        // always ensure useage of forward slash
        P = P.Replace('\\', '/');
        P = P.Replace(@"\\", "/");

        // Paths never end with a slash '/'
        if (P.EndsWith("/"))
        {
            P = P.Substring(0, P.Length - 1);
        }
    }

    public static implicit operator string(PhxPath p) => p.P;
    public static implicit operator PhxPath(string p) => new PhxPath(p);

    public static PhxPath operator /(PhxPath lhs, PhxPath rhs) => Concat(lhs, rhs);
    public static PhxPath operator -(PhxPath lhs, PhxPath rhs) => Remove(lhs, rhs);

    public bool Exists() => File.Exists(P) || Directory.Exists(P);

    public override string ToString()
    {
        return P;
    }

    public static PhxPath Concat(PhxPath lhs, PhxPath rhs)
    {
        PhxPath path = new PhxPath(lhs);
        if (!rhs.P.StartsWith("/"))
        {
            path.P += '/';
        }
        path.P += rhs.P;
        return path;
    }

    public static PhxPath Remove(PhxPath lhs, PhxPath rhs)
    {
        PhxPath path = new PhxPath(lhs);
        path.P = path.P.Replace(rhs.P, "");
        if (path.P.StartsWith("/"))
        {
            path.P = path.P.Substring(1, path.P.Length - 1);
        }
        if (path.P.EndsWith("/"))
        {
            path.P = path.P.Substring(0, path.P.Length - 1);
        }
        return path;
    }

    public bool IsFile()
    {
        try
        {
            FileAttributes attr = File.GetAttributes(P);
            return attr != FileAttributes.Directory;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public bool HasExtension(string extension)
    {
        return IsFile() && P.EndsWith(extension, StringComparison.InvariantCultureIgnoreCase);
    }

    // nodeCount: how many nodes to return, starting counting from leaf node
    public PhxPath GetLeaf(int nodeCount)
    {
        Debug.Assert(nodeCount > 0);
        string[] nodes = P.Split('/');
        nodeCount = Mathf.Min(nodeCount, nodes.Length);
        PhxPath result = "";
        for (int i = nodes.Length - nodeCount; i < nodes.Length; ++i)
        {
            result /= nodes[i];
        }
        if (result.P.StartsWith("/"))
        {
            result.P = result.P.Substring(1, result.P.Length - 1);
        }
        return result;
    }

    public PhxPath GetLeaf()
    {
        return GetLeaf(1);
    }

    public bool Contains(PhxPath p)
    {
        return P.IndexOf(p.P, StringComparison.InvariantCultureIgnoreCase) != -1;
    }

    /// <summary>
    /// The same path as it actually exists on disk, ignoring case, or null if
    /// no such file or directory exists.
    /// </summary>
    /// <remarks>
    /// Mods are authored on Windows, where case never mattered, and they ship
    /// the casing the mod tools produced: the Conversion Pack installs
    /// <c>addon/BF1/data/_LVL_PC/SIDE/patch.lvl</c>. The runtime asks for
    /// <c>data/_lvl_pc/side/patch.lvl</c> (relative paths are lower-cased on
    /// purpose, see <see cref="PhxEnvironment.ScheduleRel"/>), which resolves
    /// on Windows and on a case-sensitive file system does not - so on Linux
    /// every addon's data root simply "did not exist" and its maps loaded with
    /// stock data only, or not at all.
    ///
    /// Walks node by node and only enumerates a directory when the exact name
    /// misses, so the fast path costs one existence check. Callers should try
    /// the path as given first; this is the fallback.
    /// </remarks>
    public PhxPath ResolveCaseInsensitive()
    {
        if (Exists()) return this;

        string[] nodes = P.Split('/');
        if (nodes.Length == 0) return null;

        // An absolute path splits with an empty first node ("/a/b" -> "", "a", "b").
        bool absolute = nodes[0].Length == 0;
        string current = absolute ? "/" : nodes[0];

        if (!absolute && !Directory.Exists(current) && !File.Exists(current))
        {
            // A relative root we cannot even find the start of - not worth
            // guessing what the working directory meant.
            return null;
        }

        for (int i = 1; i < nodes.Length; ++i)
        {
            if (nodes[i].Length == 0) continue;

            string exact = current.EndsWith("/") ? current + nodes[i] : current + "/" + nodes[i];
            if (Directory.Exists(exact) || File.Exists(exact))
            {
                current = exact;
                continue;
            }

            if (!Directory.Exists(current)) return null;

            string match = null;
            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(current))
                {
                    string leaf = Path.GetFileName(entry.TrimEnd('/', '\\'));
                    if (string.Equals(leaf, nodes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        match = entry;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable directory (permissions, a dangling symlink into a
                // mod that was moved) is a miss, not a crash.
                return null;
            }

            if (match == null) return null;
            current = match.Replace('\\', '/');
        }

        return new PhxPath(current);
    }

    /// <summary>
    /// This path if it exists, otherwise the case-corrected one, otherwise
    /// this path unchanged so callers can report what was actually asked for.
    /// </summary>
    public PhxPath OnDisk()
    {
        if (Exists()) return this;
        return ResolveCaseInsensitive() ?? this;
    }

    public override int GetHashCode()
    {
        return P.GetHashCode();
    }

    public override bool Equals(object obj)
    {
        PhxPath other = obj as PhxPath;
        if (other == null)
        {
            return false;
        }
        return other.GetHashCode() == GetHashCode();
    }
}