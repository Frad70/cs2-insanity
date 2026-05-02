using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace InsanityRevive;

// Shared-memory pool mirror of C++ Pool (src/pool_format.h in InsanityHider).
// Layout v2 (3972 bytes total, little-endian):
//   [   0..   3] magic = 0x46534E49 ('INSF')
//   [   4..   7] version = 2
//   [   8..  11] activeFlag (uint32: 0 = hider disabled, 1 = enabled)
//   [  12.. 131] managed[120] (uint8: 0 = unmanaged, 1 = our fake-client)
//   [ 132..3971] names[120][32] (null-terminated UTF-8, max 31 chars + NUL)
//
// CSSharp side OWNS the pool — opens, validates/initializes, and writes
// per-slot management byte AND persona name. C++ side (InsanityHider)
// reads only. Single-uint8 management writes are atomic; name writes are
// 32-byte memcpy — torn reads are possible across the boundary, so name
// readers should treat trailing junk past the NUL as benign.
public sealed class PoolMmap : IDisposable
{
    public const uint Magic         = 0x46534E49u;
    public const uint Version       = 2u;
    public const int  Slots         = 120;
    public const int  HeaderBytes   = 12;
    public const int  ActiveOffset  = 8;
    public const int  ManagedOffset = HeaderBytes;
    public const int  NamesOffset   = ManagedOffset + Slots;   // 132
    public const int  NameBytes     = 32;
    public const int  Total         = NamesOffset + (Slots * NameBytes);

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
                // Stale or older-version pool — full reinit. v1 had no names
                // segment; v2 needs it. Don't try to migrate — just zero.
                Log.Warn($"PoolMmap reinit (magic=0x{magic:X8} ver={version} → v{Version})");
                _va.Write(0, Magic);
                _va.Write(4, Version);
                _va.Write(ActiveOffset, 1u);
                ZeroManaged();
                ZeroNames();
            }
            else
            {
                // Valid v2 pool — fresh boot still wants clean slate.
                _va.Write(ActiveOffset, 1u);
                ZeroManaged();
                ZeroNames();
            }
            Log.Info($"PoolMmap opened: {path} (v{Version}, {Slots} slots, active=1)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"PoolMmap open failed: {ex.Message}");
            Close();
            return false;
        }
    }

    private void ZeroManaged()
    {
        if (_va == null) return;
        for (int i = 0; i < Slots; i++) _va.Write(ManagedOffset + i, (byte)0);
    }

    private void ZeroNames()
    {
        if (_va == null) return;
        for (int i = 0; i < Slots * NameBytes; i++) _va.Write(NamesOffset + i, (byte)0);
    }

    public void Write(int slot, byte val)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= Slots) return;
        _va.Write(ManagedOffset + slot, val);
    }

    public byte Read(int slot)
    {
        if (_va == null) return 0;
        if (slot < 0 || slot >= Slots) return 0;
        return _va.ReadByte(ManagedOffset + slot);
    }

    public void WriteActive(bool active)
    {
        if (_va == null) return;
        _va.Write(ActiveOffset, active ? 1u : 0u);
    }

    public bool ReadActive()
    {
        if (_va == null) return false;
        return _va.ReadUInt32(ActiveOffset) != 0;
    }

    // Write persona name into per-slot 32-byte buffer. Truncates to 31 chars
    // and always null-terminates. Empty/null name clears the slot's name.
    public void WriteName(int slot, string? name)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= Slots) return;
        var bytes = new byte[NameBytes];  // zero-init
        if (!string.IsNullOrEmpty(name))
        {
            var src = Encoding.UTF8.GetBytes(name);
            int n = Math.Min(src.Length, NameBytes - 1);
            Array.Copy(src, bytes, n);
            // bytes[n..] already 0 from new byte[].
        }
        int baseOff = NamesOffset + slot * NameBytes;
        for (int i = 0; i < NameBytes; i++) _va.Write(baseOff + i, bytes[i]);
    }

    public string ReadName(int slot)
    {
        if (_va == null) return "";
        if (slot < 0 || slot >= Slots) return "";
        int baseOff = NamesOffset + slot * NameBytes;
        var bytes = new byte[NameBytes];
        for (int i = 0; i < NameBytes; i++) bytes[i] = _va.ReadByte(baseOff + i);
        int len = 0;
        while (len < NameBytes && bytes[len] != 0) len++;
        return Encoding.UTF8.GetString(bytes, 0, len);
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
