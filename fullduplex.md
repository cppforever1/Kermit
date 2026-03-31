# Full duplex example

This document shows how two console applications can send and receive files at the same time over one serial connection.

## Goal

Use one serial link between two peers:
- Console App A
- Console App B

Each side will:
- open one serial port
- start its Kermit session
- send one file
- receive one file
- do both operations concurrently

## Serial connection

Example pair:
- Windows virtual null modem pair: `COM1` <-> `COM2`
- Linux pair: `/dev/ttyS0` <-> `/dev/ttyS1`
- USB serial adapters connected back-to-back with proper hardware wiring

Both sides must use the same serial settings.

Example settings:
- baud rate: `115200`
- parity: `None`
- data bits: `8`
- stop bits: `One`

## Important note

The current library supports full-duplex packet traffic and can read and write on the same session at the same time.

The example below demonstrates how to start one send flow and one receive flow concurrently on each side.

## Console App A example

```csharp
using KermitClient;
using KermitCommon;

var transport = new SerialPortTransport("COM1", 115200);
var session = new KermitClientSession(
    transport,
    new KermitClientOptions
    {
        DownloadDirectory = Path.Combine(Environment.CurrentDirectory, "downloads"),
        ResponseTimeoutMilliseconds = 5000,
        MaxRetries = 5
    });

session.PacketSent += (_, e) => Console.WriteLine($"A SENT  {e.Packet.Type} seq={e.Packet.Sequence}");
session.PacketReceived += (_, e) => Console.WriteLine($"A RECV  {e.Packet.Type} seq={e.Packet.Sequence}");
session.UploadProgressChanged += (_, e) =>
    Console.WriteLine($"A UP    {e.Progress.FileName} {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
session.DownloadProgressChanged += (_, e) =>
    Console.WriteLine($"A DOWN  {e.Progress.FileName} {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
session.UploadCompleted += (_, e) => Console.WriteLine($"A upload completed: {e.FullPath}");
session.DownloadCompleted += (_, e) => Console.WriteLine($"A download completed: {e.FullPath}");
session.ErrorReceived += (_, e) => Console.WriteLine($"A error: {e.Message}");

await session.OpenAsync();

var sendTask = session.SendFileAsync(@"C:\data\from-a.txt", "from-a.txt");
var receiveTask = session.RequestFileAsync("from-b.txt", @"C:\data\downloads\from-b.txt");

await Task.WhenAll(sendTask, receiveTask);

await session.CloseAsync();
await session.DisposeAsync();
```

## Console App B example

```csharp
using KermitCommon;
using KermitServer;

var transport = new SerialPortTransport("COM2", 115200);
var session = new KermitServerSession(
    transport,
    new KermitServerOptions
    {
        RootDirectory = Path.Combine(Environment.CurrentDirectory, "storage"),
        ResponseTimeoutMilliseconds = 5000,
        MaxRetries = 5
    });

session.PacketSent += (_, e) => Console.WriteLine($"B SENT  {e.Packet.Type} seq={e.Packet.Sequence}");
session.PacketReceived += (_, e) => Console.WriteLine($"B RECV  {e.Packet.Type} seq={e.Packet.Sequence}");
session.UploadProgressChanged += (_, e) =>
    Console.WriteLine($"B UP    {e.Progress.FileName} {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
session.DownloadProgressChanged += (_, e) =>
    Console.WriteLine($"B DOWN  {e.Progress.FileName} {e.Progress.BytesTransferred}/{e.Progress.TotalBytes}");
session.UploadCompleted += (_, e) => Console.WriteLine($"B upload completed: {e.FullPath}");
session.DownloadCompleted += (_, e) => Console.WriteLine($"B download completed: {e.FullPath}");
session.ErrorReceived += (_, e) => Console.WriteLine($"B error: {e.Message}");

await session.OpenAsync();

var sendTask = session.SendFileAsync(@"C:\data\from-b.txt", "from-b.txt");

Console.WriteLine("B is ready. It can receive uploads and serve GET requests at the same time.");
await sendTask;

await session.CloseAsync();
await session.DisposeAsync();
```

## How the simultaneous flow works

In this example:
- App A uploads `from-a.txt` to App B
- App A also requests `from-b.txt` from App B
- App B sends `from-b.txt` while also receiving `from-a.txt`

That means the same serial connection is carrying:
- A -> B data packets for one file
- B -> A data packets for another file
- acknowledgements in both directions

## Suggested startup order

1. Start Console App B first
2. Start Console App A second
3. Watch both consoles for packet flow and progress messages

## Example directory layout

### App A machine

```text
C:\data\from-a.txt
C:\data\downloads\
```

### App B machine

```text
C:\data\from-b.txt
<working-folder>\storage\
```

Copy `from-b.txt` into App B `RootDirectory` or adjust the server code to point `RootDirectory` at the folder that contains it.

## Minimal project file references

### Console App A

```xml
<ItemGroup>
  <ProjectReference Include="..\KermitCommon\KermitCommon.csproj" />
  <ProjectReference Include="..\KermitClient\KermitClient.csproj" />
</ItemGroup>
```

### Console App B

```xml
<ItemGroup>
  <ProjectReference Include="..\KermitCommon\KermitCommon.csproj" />
  <ProjectReference Include="..\KermitServer\KermitServer.csproj" />
</ItemGroup>
```

## Tips

- Use a virtual serial pair during testing.
- Make sure both peers use the same baud rate and framing.
- Keep file names simple while testing.
- Subscribe to `PacketSent` and `PacketReceived` to observe duplex activity.
- If needed, increase `ResponseTimeoutMilliseconds` for slower links.

## Current limitation

This example shows concurrent bidirectional transfer usage with the current library design.

For heavy production use, additional Kermit features and stronger simultaneous transfer coordination may still be desirable.
