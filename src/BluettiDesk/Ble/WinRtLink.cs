using System.Runtime.InteropServices.WindowsRuntime;
using BluettiDesk.Core.Protocol;
using BluettiDesk.Core.Station;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluettiDesk.Ble;

/// <summary>The station's GATT service, reached through the Windows Bluetooth stack.</summary>
internal sealed class WinRtLink : IStationLink
{
    private static readonly Guid ServiceUuid = new("0000ff00-0000-1000-8000-00805f9b34fb");
    private static readonly Guid NotifyUuid = new("0000ff01-0000-1000-8000-00805f9b34fb");
    private static readonly Guid WriteUuid = new("0000ff02-0000-1000-8000-00805f9b34fb");

    private readonly BluetoothLEDevice _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;

    private WinRtLink(BluetoothLEDevice device)
    {
        _device = device;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public event Action<byte[]>? NotificationReceived;

    public event Action? ConnectionLost;

    public static async Task<WinRtLink> OpenAsync(ulong address, CancellationToken cancellationToken)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)
            ?? throw new ProtocolException("Windows не може знайти станцію за її адресою.");
        var link = new WinRtLink(device);
        try
        {
            await link.DiscoverAsync(cancellationToken);
            return link;
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The handler goes first: the station sends its challenge as soon as notifications are on.
        _notify!.ValueChanged += OnValueChanged;
        var status = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
        ThrowIfFailed(status);
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        var result = await _write!.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
        ThrowIfFailed(result.Status);
    }

    public ValueTask DisposeAsync()
    {
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (_notify is not null)
        {
            _notify.ValueChanged -= OnValueChanged;
        }

        // Windows keeps the radio link up until every one of these is gone.
        _service?.Dispose();
        _session?.Dispose();
        _device.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ThrowIfFailed(GattCommunicationStatus status)
    {
        if (status != GattCommunicationStatus.Success)
        {
            throw new ProtocolException($"Помилка Bluetooth: {status}.");
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken);
        _session.MaintainConnection = true;

        var services = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        ThrowIfFailed(services.Status);
        _service = services.Services.FirstOrDefault()
            ?? throw new ProtocolException("На цьому пристрої немає сервісу станції.");

        _notify = await CharacteristicAsync(NotifyUuid, cancellationToken);
        _write = await CharacteristicAsync(WriteUuid, cancellationToken);
    }

    private async Task<GattCharacteristic> CharacteristicAsync(Guid uuid, CancellationToken cancellationToken)
    {
        var result = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        ThrowIfFailed(result.Status);
        return result.Characteristics.FirstOrDefault()
            ?? throw new ProtocolException("На станції немає потрібної характеристики Bluetooth.");
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        NotificationReceived?.Invoke(args.CharacteristicValue.ToArray());

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ConnectionLost?.Invoke();
        }
    }
}
