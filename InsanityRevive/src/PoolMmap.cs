using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace InsanityRevive;

// Shared-memory pool mirror of C++ Pool (src/pool_format.h in InsanityHider).
// Layout (132 bytes total, little-endian):
//   [ 0.. 3] magic = 0x46534E49 ('INSF')
//   [ 4.. 7] version = 1
//   [ 8..11] reserved
//   [12..131] uint8_t slots[120]: 0=unmanaged, 1=our fake-client
//
// CSSharp side OWNS the pool — opens, validates/initializes, and writes
// per-slot bytes from Spawn()/Despawn(). C++ side (InsanityHider) only
// reads. Idempotent and atomic at byte granularity (single uint8 write).
public sealed class PoolMmap : IDisposable
{
    public const uint Magic        = 0x46534E49u;
    public const uint Version      = 1u;
    public const int  Slots        = 120;
    public const int  HeaderBytes  = 12;
    public const int  Total        = HeaderBytes + Slots;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _va;
    private string _path = "";

    public bool IsOpen => _va != null;
    public string Path => _path;

    public bool Open(string path)
    {
        Close();
        _path = path;
        try
        {
            // Create or grow file to expected size.
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (fs.Length < Total) fs.SetLength(Total);
            }

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, Total, MemoryMappedFileAccess.ReadWrite);
            _va  = _mmf.CreateViewAccessor(0, Total, MemoryMappedFileAccess.ReadWrite);

            uint magic = _va.ReadUInt32(0);
            uint version = _va.ReadUInt32(4);
            if (magic != Magic || version != Version)
            {
                // Stale/empty file — reinitialize. C++ side will fail its
                // sanity check during this reinit window and silently
                // disable; the next Open from C++ catches the new state.
                Log.Warn($"PoolMmap reinit (magic=0x{magic:X8} ver={version})");
                _va.Write(0, Magic);
                _va.Write(4, Version);
                _va.Write(8, 0u);
                for (int i = 0; i < Slots; i++) _va.Write(HeaderBytes + i, (byte)0);
            }
            Log.Info($"PoolMmap opened: {path} ({Slots} slots)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"PoolMmap open failed: {ex.Message}");
            Close();
            return false;
        }
    }

    public void Write(int slot, byte val)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= Slots) return;
        _va.Write(HeaderBytes + slot, val);
    }

    public byte Read(int slot)
    {
        if (_va == null) return 0;
        if (slot < 0 || slot >= Slots) return 0;
        return _va.ReadByte(HeaderBytes + slot);
    }

    public void Close()
    {
        try { _va?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _va = null;
        _mmf = null;
    }

    public void Dispose() => Close();
}
