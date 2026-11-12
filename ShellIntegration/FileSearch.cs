using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace DesktopOrbit.ShellIntegration;

public sealed record FileHit(string Path, string Name, bool IsFolder, DateTime Modified)
{
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;
}

/// <summary>
/// Finds files and folders. First the Windows Search index (instant, covers your user folders and anything you've added
/// to indexing), then, on request, a scan of every drive by name for places the index doesn't cover.
/// </summary>
public static class FileSearch
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery",
        "node_modules", ".git", "WinSxS", "AppData", "ProgramData", "obj", "__pycache__", "site-packages"
    };

    /// <summary>Searches the Windows index by file name. Runs the query on its own thread; returns newest first.</summary>
    public static Task<List<FileHit>> QueryIndexAsync(string term, int max, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<List<FileHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(QueryIndex(term, max, cancellation));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        { IsBackground = true, Name = "FileSearch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static bool IsIndexAvailable()
    {
        try
        {
            QueryIndex("a", 1, CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<FileHit> QueryIndex(string term, int max, CancellationToken cancellation)
    {
        var safe = term.Replace("'", "''").Replace("%", string.Empty).Replace("[", string.Empty);
        var sql =
            $"SELECT TOP {max * 8} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType, System.DateModified " +
            $"FROM SystemIndex WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%{safe}%' " +
            "ORDER BY System.DateModified DESC";

        var connectionType = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("Windows Search is not available");
        dynamic connection = Activator.CreateInstance(connectionType)!;
        var hits = new List<FileHit>();
        try
        {
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            dynamic records = connection.Execute(sql);
            while (!(bool)records.EOF && hits.Count < max * 8)
            {
                cancellation.ThrowIfCancellationRequested();
                string? path = records.Fields.Item(0).Value as string;
                string? name = records.Fields.Item(1).Value as string;
                string? type = records.Fields.Item(2).Value as string;
                object modified = records.Fields.Item(3).Value;
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(name))
                {
                    hits.Add(new FileHit(path, name, string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase),
                        modified is DateTime date ? date : DateTime.MinValue));
                }

                records.MoveNext();
            }

            records.Close();
        }
        finally
        {
            try
            {
                connection.Close();
            }
            catch (Exception)
            {
            }
        }

        // Your own folders first, then names starting with the term, then newest. Programming clutter is dropped.
        return hits
            .Where(h => !IsNoise(h.Path))
            .OrderByDescending(h => InUserFolder(h.Path))
            .ThenByDescending(h => h.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.Modified)
            .Take(max)
            .ToList();
    }

    private static readonly string[] NoiseParts =
    [
        @"\site-packages\", @"\__pycache__\", @"\node_modules\", @"\.git\", @"\anaconda3\", @"\miniconda3\",
        @"\AppData\", @"\.vscode\", @"\.nuget\", @"\obj\", @"\bin\Debug\", @"\bin\Release\", @"\.cache\"
    ];

    private static readonly string[] UserFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive")
    ];

    public static bool IsNoise(string path) => NoiseParts.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool InUserFolder(string path) =>
        UserFolders.Any(folder => folder.Length > 0 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scans every fixed drive for names containing the term, skipping Windows and system folders.
    /// Results arrive through <paramref name="found"/> as they're found; stops at <paramref name="max"/>.
    /// </summary>
    public static Task ScanDrivesAsync(string term, int max, Action<FileHit> found, CancellationToken cancellation)
    {
        var count = 0;
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);
        return Task.WhenAll(drives.Select(root => Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Temporary | FileAttributes.ReparsePoint
            };

            var walk = new FileSystemEnumerable<FileHit>(root,
                (ref FileSystemEntry entry) => new FileHit(entry.ToFullPath(), entry.FileName.ToString(), entry.IsDirectory, entry.LastWriteTimeUtc.LocalDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    // Runs for every entry, so the scan stops promptly when cancelled or full.
                    if (cancellation.IsCancellationRequested || Volatile.Read(ref count) >= max)
                    {
                        throw new OperationCanceledException();
                    }

                    return entry.FileName.Contains(term, StringComparison.OrdinalIgnoreCase);
                },
                // Checked for every folder, so a cancelled or finished search stops promptly even with no matches.
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    !cancellation.IsCancellationRequested && Volatile.Read(ref count) < max &&
                    !SkippedFolders.Contains(entry.FileName.ToString()) &&
                    !(entry.FileName.StartsWith("Program Files") && entry.Directory.Length <= 3)
            };

            try
            {
                foreach (var hit in walk)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (IsNoise(hit.Path))
                    {
                        continue;
                    }

                    if (Interlocked.Increment(ref count) > max)
                    {
                        return;
                    }

                    found(hit);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }, cancellation)));
    }
}
