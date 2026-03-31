# KermitClient usage

## Purpose
`KermitClient` provides the client-side Kermit session over a serial port for Windows and Linux.

## Main types
- `KermitClientSession`
- `KermitClientOptions`
- `SerialPortTransport` from `KermitCommon`

## Basic setup
```csharp
using KermitClient;
using KermitCommon;

var transport = new SerialPortTransport("COM2", 115200);
var client = new KermitClientSession(
    transport,
    new KermitClientOptions
    {
        DownloadDirectory = Path.Combine(Environment.CurrentDirectory, "downloads"),
        ResponseTimeoutMilliseconds = 5000,
        MaxRetries = 5
    });

await client.OpenAsync();
```

On Linux, use a device path such as `/dev/ttyUSB0` or `/dev/ttyS0`.

## Send a file to server
```csharp
client.UploadProgressChanged += (_, e) =>
{
    Console.WriteLine($"Upload {e.Progress.FileName}: {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
};

client.UploadCompleted += (_, e) =>
{
    Console.WriteLine($"Upload completed: {e.FullPath}");
};

await client.SendFileAsync(@"C:\temp\sample.txt");
```

## Request a file from server
```csharp
client.DownloadProgressChanged += (_, e) =>
{
    Console.WriteLine($"Download {e.Progress.FileName}: {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
};

client.DownloadCompleted += (_, e) =>
{
    Console.WriteLine($"Downloaded to: {e.FullPath}");
};

var savedPath = await client.RequestFileAsync("remote.txt");
Console.WriteLine(savedPath);
```

## List a remote folder
```csharp
client.DirectoryListingReceived += (_, e) =>
{
    Console.WriteLine($"Remote path: {e.RemotePath}");
    foreach (var entry in e.Entries)
    {
        Console.WriteLine($"{(entry.IsDirectory ? "<DIR>" : "FILE ")} {entry.Name} {entry.Size}");
    }
};

var entries = await client.ListRemoteDirectoryAsync(".");
Console.WriteLine($"Entries: {entries.Count}");
```

## Packet and command events
Every major Kermit command already exposes events from the shared base session:
- `SendInitReceived` / `SendInitSent`
- `FileHeaderReceived` / `FileHeaderSent`
- `FileAttributesReceived` / `FileAttributesSent`
- `DataReceived` / `DataSent`
- `EndOfFileReceived` / `EndOfFileSent`
- `BreakReceived` / `BreakSent`
- `AckReceived` / `AckSent`
- `NakReceived` / `NakSent`
- `ErrorReceived` / `ErrorSent`
- `GenericCommandReceived` / `GenericCommandSent`
- `RemoteCommandReceived` / `RemoteCommandSent`
- `ReceiveInitReceived` / `ReceiveInitSent`
- `TextHeaderReceived` / `TextHeaderSent`
- `PacketReceived` / `PacketSent`

Example:
```csharp
client.AckReceived += (_, e) =>
{
    Console.WriteLine($"ACK seq={e.Packet.Sequence} msg={e.Message}");
};

client.ErrorReceived += (_, e) =>
{
    Console.WriteLine($"ERROR: {e.Message}");
};
```

## Logging
Logging is shared from `KermitCommon` through `NLog`.
No extra client logger setup is required unless custom NLog configuration is desired.

## Shutdown
```csharp
await client.CloseAsync();
await client.DisposeAsync();
```

## Notes
- Open the session before sending or requesting files.
- Use `ListRemoteDirectoryAsync()` to issue the remote `LS` command.
- Use matching serial settings on client and server.
- Current implementation supports the core transfer flow and command events.
- Advanced Kermit options such as long packets and multi-packet windows are not yet implemented.
