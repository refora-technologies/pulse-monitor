using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services.InGame;

/// <summary>
/// One reading as the in-game overlay draws it: everything already decided by Pulse.
/// </summary>
internal readonly record struct InGameTile(string Id, string Label, string Value, string Unit, byte State, bool HasBar, float BarFraction);

/// <summary>
/// Everything the in-game overlay draws in one frame: the readings, and how to lay them out.
/// </summary>
/// TargetPid is the one game allowed to draw, the game in front; any other game with the overlay
/// loaded draws nothing.
internal sealed record InGameFrame(
    bool Enabled, bool Bar, float Scale, float AnchorX, float AnchorY,
    float PanelOpacity, float OverlayOpacity, IReadOnlyList<InGameTile> Tiles, uint TargetPid = 0);

/// <summary>
/// Writes Pulse's readings into the block the in-game overlay reads (InGame/src/pulse_ingame_shared.h).
/// </summary>
/// <remarks>
/// <para>
/// The layout is written byte for byte to match the C++ structs, whose sizes and offsets are
/// checked there with static_assert; the constants below are the same numbers, and the in-game
/// tests check both sides agree.
/// </para>
/// <para>
/// Pulse runs as administrator and games do not, so the block is created with an explicit security
/// descriptor: the person signed in, and the games they run, may read it; only System and
/// administrators may write it.
/// </para>
/// <para>
/// If the block already exists when Pulse goes to create it, it is not used: its security is
/// whatever its maker chose, and writing into it, or letting the overlay read from it, could put
/// someone else's numbers in front of a player. Usually it is only a game still holding the block
/// of the Pulse that ran a moment ago, which it lets go of seconds after the readings stop, so
/// creating it is tried again a little later rather than given up on.
/// </para>
/// </remarks>
internal sealed class InGameBlockWriter : IDisposable
{
    public const string Name = @"Local\PulseIngame-v3";
    public const uint Magic = 0x504C5345;   // "PLSE"
    public const uint Version = 3;
    public const int MaxTiles = 24;

    // Byte offsets, matching the C++ structs.
    public const int TileSize = 84, TileId = 0, TileLabel = 16, TileValue = 40, TileUnit = 64, TileState = 76, TileHasBar = 77, TileBar = 80;
    public const int OffMagic = 0, OffVersion = 4, OffSequence = 8, OffTileCount = 12, OffWrittenAt = 16, OffEnabled = 24, OffLayout = 25,
                     OffScale = 28, OffAnchorX = 32, OffAnchorY = 36, OffPanel = 40, OffOverlay = 44, OffTarget = 48, OffTiles = 72;
    public const int BlockSize = OffTiles + MaxTiles * TileSize;  // 2088

    private IntPtr _mapping, _view;
    private uint _sequence;
    private long _retryAt;
    private bool _toldRefused;
    private bool _disposed;

    /// How long to wait before trying again when the block already exists.
    private const long RetryMs = 2000;

    /// <summary>
    /// Creates the block on first use. False when it cannot be, or when it existed already.
    /// </summary>
    public bool Open()
    {
        if (_view != IntPtr.Zero) return true;
        if (_disposed || Environment.TickCount64 < _retryAt) return false;

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;IU)", 1, out var sd, IntPtr.Zero))
        {
            LogService.Warn(nameof(InGameBlockWriter), $"Could not build the block's security ({Marshal.GetLastWin32Error()}).");
            return false;
        }
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            _mapping = CreateFileMappingW(new IntPtr(-1), ref sa, PAGE_READWRITE, 0, BlockSize, Name);
            int error = Marshal.GetLastWin32Error();
            if (_mapping == IntPtr.Zero)
            {
                LogService.Warn(nameof(InGameBlockWriter), $"Could not create the in-game block ({error}).");
                return false;
            }
            if (error == ERROR_ALREADY_EXISTS)
            {
                CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
                _retryAt = Environment.TickCount64 + RetryMs;
                if (!_toldRefused)
                {
                    _toldRefused = true;
                    LogService.Warn(nameof(InGameBlockWriter),
                        "The in-game block already exists, not made by this Pulse (most likely a game still holding the last one). Not used; trying again shortly.");
                }
                return false;
            }
            if (_toldRefused)
            {
                _toldRefused = false;
                LogService.Info(nameof(InGameBlockWriter), "The in-game block is this Pulse's own now.");
            }
            _view = MapViewOfFile(_mapping, FILE_MAP_WRITE, 0, 0, (UIntPtr)BlockSize);
            if (_view == IntPtr.Zero)
            {
                LogService.Warn(nameof(InGameBlockWriter), $"Could not map the in-game block ({Marshal.GetLastWin32Error()}).");
                CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
                return false;
            }
            return true;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// Writes one frame. The sequence number is odd while writing and even after, so the overlay
    /// never keeps a half-written copy.
    /// </summary>
    public void Write(InGameFrame frame)
    {
        if (!Open()) return;

        var bytes = Encode(frame);

        _sequence = (_sequence + 1) | 1;                      // odd: writing
        Marshal.WriteInt32(_view, OffSequence, (int)_sequence);
        Thread.MemoryBarrier();
        // Everything but the sequence number itself.
        Marshal.Copy(bytes, 0, _view, OffSequence);
        Marshal.Copy(bytes, OffSequence + 4, _view + OffSequence + 4, BlockSize - OffSequence - 4);
        Thread.MemoryBarrier();
        _sequence++;                                          // even: done
        Marshal.WriteInt32(_view, OffSequence, (int)_sequence);
    }

    /// <summary>
    /// The block's bytes for a frame, without the sequence number. Separate so tests can check
    /// the layout without creating anything.
    /// </summary>
    internal static byte[] Encode(InGameFrame frame)
    {
        var b = new byte[BlockSize];
        BitConverter.TryWriteBytes(b.AsSpan(OffMagic), Magic);
        BitConverter.TryWriteBytes(b.AsSpan(OffVersion), Version);
        int count = Math.Min(frame.Tiles.Count, MaxTiles);
        BitConverter.TryWriteBytes(b.AsSpan(OffTileCount), (uint)count);
        BitConverter.TryWriteBytes(b.AsSpan(OffWrittenAt), GetTickCount64());
        b[OffEnabled] = frame.Enabled ? (byte)1 : (byte)0;
        b[OffLayout] = frame.Bar ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(b.AsSpan(OffScale), frame.Scale);
        BitConverter.TryWriteBytes(b.AsSpan(OffAnchorX), frame.AnchorX);
        BitConverter.TryWriteBytes(b.AsSpan(OffAnchorY), frame.AnchorY);
        BitConverter.TryWriteBytes(b.AsSpan(OffPanel), frame.PanelOpacity);
        BitConverter.TryWriteBytes(b.AsSpan(OffOverlay), frame.OverlayOpacity);
        BitConverter.TryWriteBytes(b.AsSpan(OffTarget), frame.TargetPid);

        for (int i = 0; i < count; i++)
        {
            var t = frame.Tiles[i];
            int at = OffTiles + i * TileSize;
            PutText(b, at + TileId, 16, t.Id);
            PutText(b, at + TileLabel, 24, t.Label);
            PutText(b, at + TileValue, 24, t.Value);
            PutText(b, at + TileUnit, 12, t.Unit);
            b[at + TileState] = t.State;
            b[at + TileHasBar] = t.HasBar ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(b.AsSpan(at + TileBar), Math.Clamp(t.BarFraction, 0f, 1f));
        }
        return b;
    }

    /// UTF-8, cut to fit with room for the terminating zero, and never in the middle of a
    /// character: a cut multi-byte character would draw as a broken glyph.
    internal static void PutText(byte[] into, int at, int size, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        int n = Math.Min(bytes.Length, size - 1);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--;  // back off a continuation byte
        Array.Copy(bytes, 0, into, at, n);
    }

    public void Dispose()
    {
        _disposed = true;   // never made again on the way out
        if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; }
        if (_mapping != IntPtr.Zero) { CloseHandle(_mapping); _mapping = IntPtr.Zero; }
    }

    private const uint PAGE_READWRITE = 0x04, FILE_MAP_WRITE = 0x0002;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
