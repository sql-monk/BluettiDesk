using System.IO;
using System.Text.Json;
using BluettiDesk.Core.Protocol;
using BluettiDesk.Core.Station;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace BluettiDesk.Ble;

/// <summary>Opens the station by its remembered address, or finds it by name and remembers it.</summary>
internal sealed class WinRtLinkFactory : IStationLinkFactory
{
    private const string NamePrefix = "AC180";
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluettiDesk", "settings.json");

    public async Task<IStationLink> OpenAsync(CancellationToken cancellationToken)
    {
        if (LoadAddress() is { } remembered)
        {
            try
            {
                return await WinRtLink.OpenAsync(remembered, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Out of range, or a different station now: look for one.
            }
        }

        var found = await ScanAsync(cancellationToken)
            ?? throw new ProtocolException("Станцію не знайдено. Перевірте, що вона ввімкнена, поруч і не підключена до телефона.");
        SaveAddress(found);
        return await WinRtLink.OpenAsync(found, cancellationToken);
    }

    private static async Task<ulong?> ScanAsync(CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            if (args.Advertisement.LocalName.StartsWith(NamePrefix, StringComparison.Ordinal))
            {
                result.TrySetResult(args.BluetoothAddress);
            }
        };
        watcher.Stopped += (_, args) =>
        {
            if (args.Error != BluetoothError.Success)
            {
                result.TrySetException(new ProtocolException(args.Error == BluetoothError.RadioNotAvailable
                    ? "Bluetooth вимкнено. Увімкніть його в налаштуваннях Windows."
                    : $"Не вдалося почати пошук Bluetooth: {args.Error}."));
            }
        };

        watcher.Start();
        try
        {
            return await result.Task.WaitAsync(ScanTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static ulong? LoadAddress()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Address;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveAddress(ulong address)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(address)));
    }

    private sealed record Settings(ulong? Address);
}
