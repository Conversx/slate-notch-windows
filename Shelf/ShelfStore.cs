using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Slate.Shelf;

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long ByteSize { get; init; }
    public BitmapSource? Icon { get; set; }

    public string SizeLabel => IsDirectory ? "Folder" : Format(ByteSize);

    public bool StillExists => IsDirectory ? Directory.Exists(Path) : File.Exists(Path);

    private static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static ShelfItem? For(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new ShelfItem
                {
                    Path = path,
                    Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
                    IsDirectory = true
                };
            }
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new ShelfItem { Path = path, Name = info.Name, ByteSize = info.Length };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The shelf holds <em>references</em>, never copies — dragging a file to the bar and
/// back out must leave the original exactly where it was.
/// </summary>
public sealed class ShelfStore
{
    public ObservableCollection<ShelfItem> Items { get; } = [];

    private const int Limit = 40;

    private static string StatePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Slate");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "shelf.json");
        }
    }

    public ShelfStore() => Load();

    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = ShelfItem.For(path);
            if (item is null) continue;
            item.Icon = FileIcon.For(path);
            Items.Insert(0, item);
            added++;
        }
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
        if (added > 0) Save();
        return added;
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    public void Open(ShelfItem item) => Launcher.Open(item.Path);

    public void RevealInExplorer(ShelfItem item) => Launcher.Reveal(item.Path);

    private void Save()
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(Items.Select(i => i.Path).ToArray()));
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"shelf save failed: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(StatePath)) ?? [];
            foreach (var path in paths)
            {
                var item = ShelfItem.For(path);
                // Anything that has been moved or deleted since last run is simply dropped.
                if (item is null || !item.StillExists) continue;
                item.Icon = FileIcon.For(path);
                Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"shelf load failed: {ex.Message}");
        }
    }
}
