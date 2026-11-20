using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BrainSpace;

internal sealed class Storage : IDisposable
{
    private const int Version = 1;
    private const int MaxImage = 48 * 1024 * 1024;
    private const int MaxArchive = 768 * 1024 * 1024;
    private static readonly Regex AssetName = new("^[0-9a-f]{64}\\.(png|jpg|webp)$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 24 };
    private readonly Sqlite database;
    private Content saved = new();
    private Session? savedSession;
    public string Folder { get; }
    public string Images { get; }

    public Storage(string folder)
    {
        Folder = folder;
        Images = Path.Combine(folder, "images");
        Directory.CreateDirectory(Images);
        database = new Sqlite(Path.Combine(folder, "brainspace.db"));
        try
        {
            int version = int.Parse(database.Read("PRAGMA user_version").Single().Key);
            if (version > Version) throw new InvalidDataException("This data was saved with a newer version of BrainSpace.");
            database.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000;");
            if (version == 0)
                database.Exec("BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS records (kind INTEGER NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(kind,id)); PRAGMA user_version=1; COMMIT;");
        }
        catch { database.Dispose(); throw; }
    }

    public (Content, Session?) Load()
    {
        ReadRows(1, saved.Brains);
        ReadRows(2, saved.Items);
        ReadRows(3, saved.Edges);
        string? session = database.Read("SELECT id,data FROM records WHERE kind=4").SingleOrDefault().Value;
        savedSession = session is null ? null : Parse<Session>(session);
        Validate(saved);
        return (saved.Copy(), savedSession);
    }

    private void ReadRows<T>(int kind, Dictionary<Guid, T> target)
    {
        foreach (var (key, value) in database.Read($"SELECT id,data FROM records WHERE kind={kind}"))
            target.Add(Guid.Parse(key), Parse<T>(value));
    }

    public void Save(Content content, Session session)
    {
        database.Exec("BEGIN IMMEDIATE");
        try
        {
            using var upsert = database.Prepare("INSERT INTO records(kind,id,data) VALUES (?1,?2,?3) ON CONFLICT(kind,id) DO UPDATE SET data=excluded.data");
            using var delete = database.Prepare("DELETE FROM records WHERE kind=?1 AND id=?2");
            Sync(1, saved.Brains, content.Brains);
            Sync(2, saved.Items, content.Items);
            Sync(3, saved.Edges, content.Edges);
            if (savedSession != session) upsert.Write("4", "session", JsonSerializer.Serialize(session, Json));
            database.Exec("COMMIT");
            saved = content;
            savedSession = session;

            void Sync<T>(int kind, Dictionary<Guid, T> old, Dictionary<Guid, T> current)
            {
                string key = kind.ToString();
                foreach (Guid id in old.Keys)
                    if (!current.ContainsKey(id)) delete.Write(key, id.ToString());
                foreach (var (id, value) in current)
                    if (!old.TryGetValue(id, out T? previous) || !Equals(previous, value))
                        upsert.Write(key, id.ToString(), JsonSerializer.Serialize(value, Json));
            }
        }
        catch { database.Exec("ROLLBACK"); throw; }
    }

    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Empty BrainSpace record.");

    public (string Name, int Width, int Height) StoreImage(Stream input)
    {
        byte[] data = ReadLimited(input, MaxImage);
        string extension = ImageType(data);
        using var image = new MemoryStream(data, writable: false);
        BitmapDecoder decoder;
        try { decoder = BitmapDecoder.Create(image, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand); }
        catch (NotSupportedException error) when (extension == ".webp")
        { throw new NotSupportedException("WebP requires Windows’ WebP Image Extensions codec.", error); }
        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 16_384 || frame.PixelHeight is < 1 or > 16_384 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000)
            throw new InvalidDataException("Use an image with fewer than 64 million pixels.");
        string name = Convert.ToHexStringLower(SHA256.HashData(data)) + extension;
        WriteAsset(name, data);
        return (name, frame.PixelWidth, frame.PixelHeight);
    }

    public BitmapSource? LoadImage(string name, int decodeWidth)
    {
        if (!AssetName.IsMatch(name)) return null;
        string path = Path.Combine(Images, name);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        BitmapFrame source = decoder.Frames[0];
        int width = source.PixelWidth, height = source.PixelHeight;
        if (width is < 1 or > 16_384 || height is < 1 or > 16_384 || (long)width * height > 64_000_000) return null;
        input.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, decodeWidth);
        else bitmap.DecodePixelHeight = Math.Min(height, decodeWidth);
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void WriteAsset(string name, byte[] data)
    {
        string path = Path.Combine(Images, name);
        if (File.Exists(path)) return;
        string temp = Path.Combine(Images, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Export(string path, Content content, Session session)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (Stream manifest = archive.CreateEntry("brainspace.json").Open())
                    JsonSerializer.Serialize(manifest, new Archive(Version, content, session), Json);
                foreach (string name in Assets(content))
                    archive.CreateEntryFromFile(Path.Combine(Images, name), "images/" + name, CompressionLevel.NoCompression);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public (Content Content, Session? Session) Import(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10_000 || archive.Entries.Any(entry => entry.Length > MaxImage) ||
            archive.Entries.Sum(entry => entry.Length) > MaxArchive)
            throw new InvalidDataException("This archive exceeds the supported import size.");
        Dictionary<string, ZipArchiveEntry> entries = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        if (!entries.TryGetValue("brainspace.json", out var manifest)) throw new InvalidDataException("No BrainSpace manifest was found.");
        using var input = manifest.Open();
        Archive imported = JsonSerializer.Deserialize<Archive>(ReadLimited(input, MaxImage), Json) ?? throw new InvalidDataException("The archive is empty.");
        if (imported.Version != Version) throw new InvalidDataException("This BrainSpace archive version is unsupported.");
        Validate(imported.Content);
        string[] assets = Assets(imported.Content).ToArray();
        foreach (string name in assets)
        {
            if (!entries.TryGetValue("images/" + name, out var entry)) throw new InvalidDataException("The archive is missing an image.");
            using Stream asset = entry.Open();
            byte[] data = ReadLimited(asset, MaxImage);
            if (Convert.ToHexStringLower(SHA256.HashData(data)) + ImageType(data) != name)
                throw new InvalidDataException("An image in the archive is damaged.");
            WriteAsset(name, data);
        }
        return (imported.Content, imported.Session);
    }

    private static IEnumerable<string> Assets(Content content) => content.Items.Values.Where(item => item.Kind == ItemKind.Image)
        .Select(item => item.Target).Distinct(StringComparer.Ordinal);

    private static void Validate(Content content)
    {
        if (content.Brains is null || content.Items is null || content.Edges is null) throw new InvalidDataException("Missing workspace records.");
        var visited = new HashSet<Guid> { Guid.Empty };
        var children = content.Brains.Values.ToLookup(brain => brain.Parent);
        var pending = new Queue<Guid>();
        pending.Enqueue(Guid.Empty);
        while (pending.TryDequeue(out Guid id))
            foreach (Brain brain in children[id])
                if (!visited.Add(brain.Id)) throw new InvalidDataException("The Brain hierarchy contains duplicate or circular IDs.");
                else pending.Enqueue(brain.Id);
        if (visited.Count != content.Brains.Count + 1) throw new InvalidDataException("The Brain hierarchy contains missing parents or cycles.");
        foreach (var (id, brain) in content.Brains)
            if (id != brain.Id || string.IsNullOrWhiteSpace(brain.Title) || brain.Title.Length > 160 ||
                !ValidCoordinate(brain.X) || !ValidCoordinate(brain.Y) || !ValidCoordinate(brain.BoardX) || !ValidCoordinate(brain.BoardY) ||
                (brain.GlobePosition is { } globe && !(Math.Abs(globe.X * globe.X + globe.Y * globe.Y + globe.Z * globe.Z - 1) < .0001)))
                throw new InvalidDataException("The archive contains an invalid Brain.");
        foreach (var (id, item) in content.Items)
            if (item is null || id == Guid.Empty || id != item.Id || content.Brains.ContainsKey(id) || !content.Brains.ContainsKey(item.Board) || !ValidItem(item))
                throw new InvalidDataException("The archive contains an invalid board item.");
        foreach (var (id, edge) in content.Edges)
            if (id != edge.Id || edge.From == edge.To || !content.Brains.ContainsKey(edge.From) || !content.Brains.ContainsKey(edge.To))
                throw new InvalidDataException("The archive contains an invalid connection.");
    }

    private static bool ValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9;

    internal static bool ValidItem(Item item) => Enum.IsDefined(item.Kind) && ValidCoordinate(item.X) && ValidCoordinate(item.Y) &&
        item.Width is >= 0 and <= 2e7 && item.Height is >= 0 and <= 2e7 && item.FontSize is >= 8 and <= 144 &&
        item.Weight is >= .5 and <= 128 && item.Opacity is >= 0 and <= 1 && item.Text is { Length: <= 120_000 } &&
        item.Target is { Length: <= 4096 } && item.Color is { Length: <= 7 } &&
        (item.Kind != ItemKind.Image || (item.Width > 0 && item.Height > 0 && AssetName.IsMatch(item.Target))) &&
        (item.Points is null || (item.Points.Length <= 32_000 && item.Points.All(point => ValidCoordinate(point.X) && ValidCoordinate(point.Y))));

    private static byte[] ReadLimited(Stream input, int max)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > max) throw new InvalidDataException("This file exceeds the supported size.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string ImageType(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..].StartsWith("WEBP"u8)) return ".webp";
        throw new InvalidDataException("Choose a PNG, JPEG, or WebP image.");
    }

    public void Dispose() => database.Dispose();
    private sealed record Archive(int Version, Content Content, Session? Session);
}

internal sealed class Sqlite : IDisposable
{
    private const int Row = 100, Done = 101;
    private IntPtr handle;

    public Sqlite(string path)
    {
        int result = Native.Open(path, out handle, 0x0002 | 0x0004 | 0x10000, IntPtr.Zero);
        if (result == 0) return;
        string error = Error();
        Dispose();
        throw new IOException(error);
    }

    private string Error() => Marshal.PtrToStringUTF8(Native.Error(handle)) ?? "SQLite could not read or write BrainSpace data.";
    private void Check(int result) { if (result != 0) throw new IOException(Error()); }
    public void Exec(string sql) => Check(Native.Exec(handle, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    public Statement Prepare(string sql)
    {
        Check(Native.Prepare(handle, sql, -1, out IntPtr statement, IntPtr.Zero));
        return new(this, statement);
    }

    public IEnumerable<(string Key, string Value)> Read(string sql)
    {
        using Statement statement = Prepare(sql);
        while (statement.Step() == Row) yield return (statement.Text(0), statement.Text(1));
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Native.Close(handle);
        handle = IntPtr.Zero;
    }

    internal sealed class Statement(Sqlite database, IntPtr handle) : IDisposable
    {
        public int Step()
        {
            int result = Native.Step(handle);
            if (result is not (Row or Done)) database.Check(result);
            return result;
        }
        public string Text(int index) => Marshal.PtrToStringUTF8(Native.Text(handle, index)) ?? "";
        public void Write(params string[] values)
        {
            for (int i = 0; i < values.Length; i++) database.Check(Native.Bind(handle, i + 1, values[i], -1, new IntPtr(-1)));
            Step();
            database.Check(Native.Reset(handle));
        }
        public void Dispose() => Native.Finalize(handle);
    }

    private static class Native
    {
        private const string Dll = "winsqlite3.dll";
        [DllImport(Dll, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)] public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
        [DllImport(Dll, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)] public static extern int Close(IntPtr database);
        [DllImport(Dll, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Error(IntPtr database);
        [DllImport(Dll, EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)] public static extern int Exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);
        [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)] public static extern int Prepare(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] public static extern int Step(IntPtr statement);
        [DllImport(Dll, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Text(IntPtr statement, int column);
        [DllImport(Dll, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)] public static extern int Bind(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
        [DllImport(Dll, EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)] public static extern int Reset(IntPtr statement);
        [DllImport(Dll, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] public static extern int Finalize(IntPtr statement);
    }
}
