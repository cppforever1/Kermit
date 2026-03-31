# KermitServer usage

## Purpose
`KermitServer` provides the server-side Kermit session over a serial port for Windows and Linux.

## Main types
- `KermitServerSession`
- `KermitServerOptions`
- `SerialPortTransport` from `KermitCommon`

## Basic setup
```csharp
using KermitCommon;
using KermitServer;

var transport = new SerialPortTransport("COM1", 115200);
var server = new KermitServerSession(
    transport,
    new KermitServerOptions
    {
        RootDirectory = Path.Combine(Environment.CurrentDirectory, "storage"),
        ResponseTimeoutMilliseconds = 5000,
        MaxRetries = 5
    });

await server.OpenAsync();
```

On Linux, use a device path such as `/dev/ttyUSB0` or `/dev/ttyS0`.

## Receive uploads from client
When the client sends a file, the server writes it into `RootDirectory`.

```csharp
server.UploadProgressChanged += (_, e) =>
{
    Console.WriteLine($"Upload {e.Progress.FileName}: {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
};

server.UploadCompleted += (_, e) =>
{
    Console.WriteLine($"Stored at: {e.FullPath}");
};
```

## Send a file to client
The server can proactively send a file:

```csharp
await server.SendFileAsync(@"C:\data\firmware.bin");
```

## Client GET command support
The server already handles a generic client command in the form:

```text
GET remote-file-name
```

If the file exists under `RootDirectory`, the server acknowledges the request and sends the file.

## Client LS command support
The server also handles remote folder listing with:

```text
LS
LS folder-name
```

The server streams the directory listing back to the client as text and raises `DirectoryListingSent`.

## Remote command support
The server currently handles this remote command form:

```text
DELETE file-name
```

If the file exists under `RootDirectory`, it is deleted and acknowledged.

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
server.GenericCommandReceived += (_, e) =>
{
    Console.WriteLine($"Generic command: {e.Command}");
};

server.DirectoryListingSent += (_, e) =>
{
    Console.WriteLine($"Listed {e.RemotePath}: {e.Entries.Count} entries");
};

server.RemoteCommandReceived += (_, e) =>
{
    Console.WriteLine($"Remote command: {e.Command}");
};
```

## Logging
Logging is shared from `KermitCommon` through `NLog`.
No extra server logger setup is required unless custom NLog configuration is desired.

## Shutdown
```csharp
await server.CloseAsync();
await server.DisposeAsync();
```

## Notes
- Open the session before transferring files.
- Use identical serial settings on both peers.
- The server can both receive files and send files back to the client.
- Advanced Kermit extensions are not yet implemented.
