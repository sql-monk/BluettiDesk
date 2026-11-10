using System.Text.Json;

namespace BluettiDesk.Core.Outages;

/// <summary>A regional DTEK site whose shutdowns page has the address form and the schedule.</summary>
public sealed record DtekSite(string Name, string Url)
{
    public static IReadOnlyList<DtekSite> All { get; } =
    [
        new("Київська область (ДТЕК Київські регіональні електромережі)", "https://www.dtek-krem.com.ua/ua/shutdowns"),
        new("Київ (ДТЕК Київські електромережі)", "https://www.dtek-kem.com.ua/ua/shutdowns"),
        new("Дніпропетровська область (ДТЕК Дніпровські електромережі)", "https://www.dtek-dnem.com.ua/ua/shutdowns"),
        new("Одеська область (ДТЕК Одеські електромережі)", "https://www.dtek-oem.com.ua/ua/shutdowns"),
    ];

    public static DtekSite Default => All[0];

    public static DtekSite ByUrl(string? url) => All.FirstOrDefault(s => s.Url == url) ?? Default;

    /// <summary>A short name for file names: "dtek-krem".</summary>
    public string Key => new Uri(Url).Host.Replace("www.", string.Empty, StringComparison.Ordinal).Split('.')[0];
}

/// <summary>Which address to follow, kept next to the journal. Nothing here is built into the app.</summary>
public sealed record OutageSettings(string SiteUrl, OutageAddress Address)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static OutageSettings Empty { get; } = new(DtekSite.Default.Url, OutageAddress.Empty);

    public DtekSite Site => DtekSite.ByUrl(SiteUrl);

    public static OutageSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is null
                ? Empty
                : new OutageSettings(DtekSite.ByUrl(stored.Site).Url, new OutageAddress(stored.City ?? string.Empty, stored.Street ?? string.Empty, stored.House ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(SiteUrl, Address.City, Address.Street, Address.House), Options));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Stored(string? Site, string? City, string? Street, string? House);
}

/// <summary>The settlements of a DTEK site and their streets, as the site's own address form offers them.</summary>
public sealed class StreetCatalog
{
    private readonly SortedDictionary<string, string[]> _streets;

    private StreetCatalog(SortedDictionary<string, string[]> streets) => _streets = streets;

    public IReadOnlyCollection<string> Cities => _streets.Keys;

    public int Count => _streets.Count;

    public IReadOnlyList<string> StreetsOf(string city) => _streets.TryGetValue(city, out var streets) ? streets : [];

    public bool Contains(string city) => _streets.ContainsKey(city);

    /// <summary>Reads the page's <c>DisconSchedule.streets</c>: an object of settlement → array of streets.</summary>
    /// <exception cref="FormatException">The text is not such an object.</exception>
    public static StreetCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Довідник вулиць має бути об'єктом.");
            }

            var streets = new SortedDictionary<string, string[]>(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("uk-UA"), ignoreCase: false));
            foreach (var city in document.RootElement.EnumerateObject())
            {
                if (city.Value.ValueKind == JsonValueKind.Array)
                {
                    streets[city.Name] = city.Value.EnumerateArray()
                        .Where(s => s.ValueKind == JsonValueKind.String)
                        .Select(s => s.GetString()!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
            }

            if (streets.Count == 0)
            {
                throw new FormatException("Довідник вулиць порожній.");
            }

            return new StreetCatalog(streets);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Не вдалося прочитати довідник вулиць.", ex);
        }
    }

    /// <summary>
    /// The options that contain everything typed, ignoring case, those that start with it first: typing "бровар" finds
    /// "м. Бровари" the way the site's form does.
    /// </summary>
    public static IReadOnlyList<string> Suggest(IEnumerable<string> options, string typed, int limit = 50)
    {
        var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return options.Take(limit).ToList();
        }

        return options
            .Where(o => words.All(w => o.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => StartsWithWord(o, words[0]) ? 0 : 1)
            .ThenBy(o => o.Length)
            .Take(limit)
            .ToList();
    }

    private static bool StartsWithWord(string option, string word) =>
        option.Split(' ', '-', '.').Any(part => part.StartsWith(word, StringComparison.OrdinalIgnoreCase));
}
