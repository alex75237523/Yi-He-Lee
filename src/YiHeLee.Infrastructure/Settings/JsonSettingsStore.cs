using System.Text.Json;
using System.Text.Json.Serialization;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private readonly SettingsValidationService _validationService;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonSettingsStore(string settingsPath, SettingsValidationService validationService)
    {
        _settingsPath = settingsPath;
        _validationService = validationService;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = AppSettings.CreateDefault();
                _validationService.EnsureFixedSources(defaults);
                await SaveInternalAsync(defaults, cancellationToken).ConfigureAwait(false);
                return defaults;
            }

            AppSettings settings;
            await using (var stream = File.OpenRead(_settingsPath))
            {
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                           ?? AppSettings.CreateDefault();
            }

            _validationService.EnsureFixedSources(settings);
            await SaveInternalAsync(settings, cancellationToken).ConfigureAwait(false);
            return settings;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _validationService.EnsureFixedSources(settings);
            await SaveInternalAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveInternalAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
