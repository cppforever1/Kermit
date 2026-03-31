# Kermit

C#/.NET 10 class libraries that implement a Kermit-style serial file transfer stack for Windows and Linux.

## Projects

### KermitCommon
Shared protocol and transport layer.

Includes:
- Kermit packet models
- protocol constants and negotiation options
- packet encoding/decoding
- block check support
- serial transport abstraction
- shared session base class
- common events and event args
- shared `NLog` logging setup

### KermitClient
Client-side transfer library.

Includes:
- upload files to a remote server
- request files from a remote server
- list remote folders with `ls`
- get remote working directory with `pwd`
- change remote directory with `cd`
- remove remote files and directories with `rm`
- create remote directories with `mkdir`
- progress events
- per-command events for packet activity

### KermitServer
Server-side transfer library.

Includes:
- receive files from a client
- send files to a client
- handle `get <file>` generic command
- handle `ls [folder]` generic command
- handle `pwd` generic command
- handle `cd <path>` generic command
- handle `rm <path>` generic command
- handle `mkdir <path>` generic command
- handle `delete <file>` remote command
- progress events
- per-command events for packet activity

## Target framework
- .NET 10 (`net10.0`)

## Supported platforms
- Windows
- Linux

The transport uses `System.IO.Ports`, so matching serial device names must be used on each OS.

Examples:
- Windows: `COM1`, `COM2`, `COM3`
- Linux: `/dev/ttyS0`, `/dev/ttyUSB0`, `/dev/ttyACM0`

## Features
- serial port transport
- asynchronous packet processing
- full-duplex session loop
- shared protocol base for client and server
- typed events for major Kermit commands
- shared logging with `NLog`
- file upload and download flows

## Current protocol coverage
Implemented core packet flow:
- `SEND-INIT`
- `FILE`
- `ATTR`
- `DATA`
- `EOF`
- `BREAK`
- `ACK`
- `NAK`
- `ERROR`
- generic command packets
- remote command packets

Supported generic commands currently include:
- `get <file>`
- `ls`
- `ls <folder>`
- `pwd`
- `cd <path>`
- `cd ..`
- `cd /`
- `rm <path>`
- `mkdir <path>`

## Not yet implemented
This repository currently provides a strong protocol foundation, but not every advanced Kermit feature is present yet.

Examples of future enhancements:
- long packets
- sliding windows larger than 1
- repeat-count compression
- fuller remote command set
- stricter interoperability with historical Kermit variants

## Logging
`KermitCommon` configures `NLog` if no configuration already exists.

Client and server libraries reuse the same logging setup through the common project.

## Build
From the workspace root:

```powershell
dotnet build Kermit.slnx
```

## Usage guides
See:
- [clientused.md](clientused.md)
- [serverused.md](serverused.md)

## Remote folder listing example
```csharp
var entries = await client.ListRemoteDirectoryAsync(".");

foreach (var entry in entries)
{
    Console.WriteLine($"{(entry.IsDirectory ? "<DIR>" : "FILE ")} {entry.Name} {entry.Size}");
}
```

## Remote working directory example
```csharp
var remotePath = await client.GetRemoteWorkingDirectoryAsync();
Console.WriteLine(remotePath);
```

## Example client setup
```csharp
using KermitClient;
using KermitCommon;

var transport = new SerialPortTransport("COM2", 115200);
var client = new KermitClientSession(
    transport,
    new KermitClientOptions
    {
        DownloadDirectory = Path.Combine(Environment.CurrentDirectory, "downloads")
    });

await client.OpenAsync();
await client.SendFileAsync(@"C:\temp\sample.txt");
await client.CloseAsync();
await client.DisposeAsync();
```

## Example server setup
```csharp
using KermitCommon;
using KermitServer;

var transport = new SerialPortTransport("COM1", 115200);
var server = new KermitServerSession(
    transport,
    new KermitServerOptions
    {
        RootDirectory = Path.Combine(Environment.CurrentDirectory, "storage")
    });

await server.OpenAsync();
```

## Repository structure
- `KermitCommon/`
- `KermitClient/`
- `KermitServer/`
- `clientused.md`
- `serverused.md`

## Status
The solution builds successfully and is ready for further protocol expansion, testing, and integration into applications that need serial Kermit transfers.
