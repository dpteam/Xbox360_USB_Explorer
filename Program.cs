// ============================================================================
//  Xbox 360 USB Explorer — Complete Rewrite
//  Single-file Program.cs
//  Target: .NET Framework 4.0 – 4.8
//  Namespace: Xbox360_USB_Explorer
//  No external libraries. No Resources. No App.config.
//  FATX reference: https://free60.org/System-Software/Systems/FATX/
//                  https://github.com/aerosoul94/FATXTools
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
/*
[assembly: AssemblyTitle("Xbox 360 USB Explorer")]
[assembly: AssemblyDescription("FATX/XTAF file-system explorer for Xbox 360 USB drives and HDD images.")]
[assembly: AssemblyCompany("Xbox360_USB_Explorer")]
[assembly: AssemblyProduct("Xbox 360 USB Explorer")]
[assembly: AssemblyCopyright("Rewritten 2026 – original by Slasher / Darkjump")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]
[assembly: Guid("b1a2c3d4-e5f6-7890-abcd-ef1234567890")]
[assembly: ComVisible(false)]
*/
namespace Xbox360_USB_Explorer
{
    // ========================================================================
    //  1. LOGGING  (Trace → Console + File, coloured, datetime-stamped)
    // ========================================================================
    internal static class Log
    {
        private static readonly object _lock = new object();
        private static TextWriterTraceListener _fileListener;

        public static void Init()
        {
            Trace.AutoFlush = true;

            // Console listener (coloured output handled by ColoredConsoleListener)
            Trace.Listeners.Add(new ColoredConsoleListener());

            // File listener – logs\<yyyy-MM-dd_HH-mm-ss>.log
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir,
                    DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".log");
                _fileListener = new TextWriterTraceListener(logFile, "FileLog");
                Trace.Listeners.Add(_fileListener);
                Info("Log file: " + logFile);
            }
            catch (Exception ex)
            {
                Trace.Listeners.Add(new ColoredConsoleListener());
                Warn("Cannot create log file: " + ex.Message);
            }
        }

        public static void Shutdown()
        {
            try { Trace.Flush(); } catch { }
        }

        public static void Info(string msg) { Write("INFO", msg, ConsoleColor.Cyan); }
        public static void Ok(string msg) { Write("OK", msg, ConsoleColor.Green); }
        public static void Warn(string msg) { Write("WARN", msg, ConsoleColor.Yellow); }
        public static void Error(string msg) { Write("ERROR", msg, ConsoleColor.Red); }
        public static void Debug(string msg) { Write("DEBUG", msg, ConsoleColor.Magenta); }
        public static void Status(string msg) { Write("STAT", msg, ConsoleColor.White); }

        private static void Write(string level, string msg, ConsoleColor color)
        {
            string line = string.Format("[{0}] [{1}] {2}",
                DateTime.Now.ToString("HH:mm:ss.fff"), level, msg);
            lock (_lock)
            {
                ConsoleColor old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Trace.WriteLine(line);
                Console.ForegroundColor = old;
            }
        }
    }

    /// <summary>TraceListener that writes to Console (avoids double writes).</summary>
    internal sealed class ColoredConsoleListener : TraceListener
    {
        public override void Write(string message) { Console.Write(message); }
        public override void WriteLine(string message) { Console.WriteLine(message); }
    }

    // ========================================================================
    //  2. WIN32 NATIVE  (raw disk, admin check, elevation)
    // ========================================================================
    internal static class Win32Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_GEOMETRY
        {
            public long Cylinders;
            public uint MediaType;
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
            public long DiskSize
            {
                get { return Cylinders * TracksPerCylinder * SectorsPerTrack * BytesPerSector; }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern SafeFileHandle CreateFile(
            string lpFileName, FileAccess dwDesiredAccess, FileShare dwShareMode,
            IntPtr lpSecurityAttributes, FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            ref DISK_GEOMETRY lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        internal const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        internal const uint FILE_ATTRIBUTE_DEVICE = 0x00000040;

        internal static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Re-launch current exe elevated (UAC).</summary>
        internal static void ElevateAndRestart(string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Assembly.GetExecutingAssembly().Location;
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            if (args != null && args.Length > 0)
                psi.Arguments = string.Join(" ", args);
            try { Process.Start(psi); }
            catch (Win32Exception) { /* user cancelled UAC */ }
            Environment.Exit(0);
        }
    }

    // ========================================================================
    //  3. FATX CONSTANTS
    // ========================================================================
    internal static class FatxConst
    {
        public const uint MAGIC = 0x58544146; // "XTAF" big-endian
        public const int SECTOR_SIZE = 0x200;      // 512
        public const int HEADER_SIZE = 0x1000;     // 4096
        public const int DIRENT_SIZE = 0x40;       // 64
        public const int MAX_FILENAME = 42;
        public const uint FAT16_THRESHOLD = 0xFFF0;     // 65520

        public const uint CLUSTER_LAST = 0xFFFFFFFF;
        public const ushort CLUSTER_LAST16 = 0xFFFF;
        public const uint CLUSTER_FREE = 0;

        public const byte DIRENT_NEVER_USED = 0x00;
        public const byte DIRENT_NEVER_USED2 = 0xFF;
        public const byte DIRENT_DELETED = 0xE5;

        // File attributes
        public const byte ATTR_READONLY = 0x01;
        public const byte ATTR_HIDDEN = 0x02;
        public const byte ATTR_SYSTEM = 0x04;
        public const byte ATTR_DIRECTORY = 0x10;
        public const byte ATTR_ARCHIVE = 0x20;

        // USB drive layout
        public const long USB_CACHE_OFFSET = 0x8000400;   // 134218752
        public const long USB_CACHE_SIZE = 0x12000400;
        public const long USB_DATA_OFFSET = 0x20000000;  // 536870912

        // HDD layout (retail)
        public const long HDD_CACHE_OFFSET = 0x80080000;  // 2148007936
        public const long HDD_CACHE_SIZE = 0xA0E30000;
        public const long HDD_COMPAT_OFFSET = 0x120EB0000; // 4847239168
        public const long HDD_COMPAT_SIZE = 0x10000000;
        public const long HDD_DATA_OFFSET = 0x130EB0000; // 5115674624

        // Read buffer for large files
        public const int IO_BUFFER_SIZE = 1024 * 1024; // 1 MB
    }

    // ========================================================================
    //  4. ENDIAN HELPERS  (FATX is big-endian)
    // ========================================================================
    internal static class Endian
    {
        public static ushort ReadU16BE(byte[] buf, int off)
        {
            return (ushort)((buf[off] << 8) | buf[off + 1]);
        }
        public static uint ReadU32BE(byte[] buf, int off)
        {
            return (uint)((buf[off] << 24) | (buf[off + 1] << 16) |
                          (buf[off + 2] << 8) | buf[off + 3]);
        }
        public static ulong ReadU64BE(byte[] buf, int off)
        {
            return ((ulong)ReadU32BE(buf, off) << 32) | ReadU32BE(buf, off + 4);
        }
        public static void WriteU16BE(byte[] buf, int off, ushort v)
        {
            buf[off] = (byte)(v >> 8);
            buf[off + 1] = (byte)(v);
        }
        public static void WriteU32BE(byte[] buf, int off, uint v)
        {
            buf[off] = (byte)(v >> 24);
            buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8);
            buf[off + 3] = (byte)(v);
        }
    }

    // ========================================================================
    //  5. PARTITION STREAM  (abstraction over file / multi-file / raw disk)
    // ========================================================================
    internal sealed class PartitionStream : IDisposable
    {
        private Stream[] _streams;
        private long[] _lengths;
        private long _totalLength;
        private long _globalOffset;
        private long _lengthOverride;
        private int _curIdx;
        private bool _disposed;

        public long GlobalOffset
        {
            get { return _globalOffset; }
            set { _globalOffset = value; }
        }
        public long LengthOverride
        {
            get { return _lengthOverride; }
            set { _lengthOverride = value; }
        }
        public long TotalLength
        {
            get { return _lengthOverride > 0 ? _lengthOverride : _totalLength; }
        }

        // Single file
        public PartitionStream(string path)
        {
            _streams = new Stream[] { new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read) };
            InitLengths();
        }

        // Multiple files (USB Data0001, Data0002, …)
        public PartitionStream(string[] paths)
        {
            _streams = new Stream[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                _streams[i] = new FileStream(paths[i], FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            InitLengths();
        }

        // Existing stream (raw disk)
        public PartitionStream(Stream s)
        {
            _streams = new Stream[] { s };
            InitLengths();
        }

        private void InitLengths()
        {
            _lengths = new long[_streams.Length];
            _totalLength = 0;
            for (int i = 0; i < _streams.Length; i++)
            {
                _lengths[i] = _streams[i].Length;
                _totalLength += _lengths[i];
            }
            _curIdx = 0;
        }

        /// <summary>Seek to absolute offset (relative to partition start + GlobalOffset).</summary>
        public void SeekTo(long offset)
        {
            long absolute = offset + _globalOffset;
            long limit = TotalLength;

            if (absolute < 0)
                throw new IOException(string.Format(
                    "Seek before start: offset={0}, global=0x{1:X} → absolute=0x{2:X}",
                    offset, _globalOffset, absolute));

            // Мягкая проверка: позволяем читать до конца диска,
            // но логируем выход за ожидаемый размер раздела
            if (absolute > limit)
            {
                Log.Debug(string.Format(
                    "Seek beyond partition: absolute=0x{0:X}, limit=0x{1:X} (Δ={2})",
                    absolute, limit, absolute - limit));
                // Не бросаем исключение — даём прочитать,
                // а FATX-движок сам обработает мусор
            }

            if (_streams.Length == 1)
            {
                _streams[0].Position = absolute;
                _curIdx = 0;
                return;
            }

            long rem = absolute;
            for (int i = 0; i < _streams.Length; i++)
            {
                if (rem < _lengths[i])
                {
                    _curIdx = i;
                    _streams[i].Position = rem;
                    return;
                }
                rem -= _lengths[i];
            }
            _curIdx = _streams.Length - 1;
            _streams[_curIdx].Position = _lengths[_curIdx];
        }

        public int Read(byte[] buf, int off, int count)
        {
            if (_streams.Length == 1)
                return _streams[0].Read(buf, off, count);

            int total = 0;
            while (count > 0 && _curIdx < _streams.Length)
            {
                int n = _streams[_curIdx].Read(buf, off + total, count);
                if (n == 0)
                {
                    _curIdx++;
                    if (_curIdx < _streams.Length) _streams[_curIdx].Position = 0;
                    continue;
                }
                total += n;
                count -= n;
            }
            return total;
        }

        public void Write(byte[] buf, int off, int count)
        {
            if (_streams.Length == 1)
            {
                _streams[0].Write(buf, off, count);
                return;
            }
            while (count > 0 && _curIdx < _streams.Length)
            {
                long remaining = _lengths[_curIdx] - _streams[_curIdx].Position;
                int chunk = (int)Math.Min(count, remaining);
                if (chunk <= 0)
                {
                    _curIdx++;
                    if (_curIdx < _streams.Length) _streams[_curIdx].Position = 0;
                    continue;
                }
                _streams[_curIdx].Write(buf, off, chunk);
                off += chunk;
                count -= chunk;
            }
        }

        public byte[] ReadBytes(int count)
        {
            byte[] buf = new byte[count];
            int read = Read(buf, 0, count);
            if (read < count)
                Array.Resize(ref buf, read);
            return buf;
        }

        public void WriteBytes(byte[] data)
        {
            Write(data, 0, data.Length);
        }

        public void Flush()
        {
            for (int i = 0; i < _streams.Length; i++)
                _streams[i].Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _streams.Length; i++)
            {
                try { _streams[i].Dispose(); } catch { }
            }
        }
    }

    // ========================================================================
    //  6. RAW DISK STREAM  (Win32 physical drive)
    // ========================================================================
    internal sealed class RawDiskStream : Stream
    {
        private SafeFileHandle _handle;
        private FileStream _fs;
        private long _diskSize;
        private long _pos;
        private byte[] _sectorBuf = new byte[FatxConst.SECTOR_SIZE];
        private long _cachedSector = -1;
        private bool _dirty;

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override long Length { get { return _diskSize; } }
        public override long Position
        {
            get { return _pos; }
            set { _pos = value; }
        }

        public RawDiskStream(int driveIndex)
        {
            string path = @"\\.\PhysicalDrive" + driveIndex;
            _handle = Win32Native.CreateFile(path,
                FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero,
                FileMode.Open,
                Win32Native.FILE_FLAG_NO_BUFFERING |
                Win32Native.FILE_FLAG_WRITE_THROUGH |
                Win32Native.FILE_ATTRIBUTE_DEVICE,
                IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new IOException("Cannot open " + path +
                    " (error " + Marshal.GetLastWin32Error() + "). Run as Administrator.");

            Win32Native.DISK_GEOMETRY geo = new Win32Native.DISK_GEOMETRY();
            uint ret;
            bool geoOk = Win32Native.DeviceIoControl(_handle,
                Win32Native.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                IntPtr.Zero, 0, ref geo,
                (uint)Marshal.SizeOf(typeof(Win32Native.DISK_GEOMETRY)),
                out ret, IntPtr.Zero);

            if (geoOk && geo.DiskSize > 0)
            {
                _diskSize = geo.DiskSize;
            }
            else
            {
                // Fallback: IOCTL_DISK_GET_LENGTH_INFO (0x0007405C)
                long len = 0;
                uint ret2;
                bool lenOk = DeviceIoControl_GetLength(_handle, out len, out ret2);
                if (lenOk && len > 0)
                {
                    _diskSize = len;
                    Log.Info("Disk size via GET_LENGTH_INFO: " + len);
                }
                else
                {
                    _diskSize = 0;
                    Log.Warn("Cannot determine disk size for " + path);
                }
            }

            _fs = new FileStream(_handle, FileAccess.ReadWrite, FatxConst.SECTOR_SIZE);
            _pos = 0;
        }

        // Дополнительный P/Invoke для fallback
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            out long lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private static bool DeviceIoControl_GetLength(SafeHandle h, out long length, out uint ret)
        {
            const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
            return DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, out length, 8, out ret, IntPtr.Zero);
        }

        private void FlushSector()
        {
            if (_dirty && _cachedSector >= 0)
            {
                _fs.Position = _cachedSector * FatxConst.SECTOR_SIZE;
                _fs.Write(_sectorBuf, 0, FatxConst.SECTOR_SIZE);
                _fs.Flush();
                _dirty = false;
            }
        }

        private void LoadSector(long sector)
        {
            if (sector == _cachedSector) return;
            FlushSector();
            _cachedSector = sector;
            _fs.Position = sector * FatxConst.SECTOR_SIZE;
            int r = _fs.Read(_sectorBuf, 0, FatxConst.SECTOR_SIZE);
            if (r < FatxConst.SECTOR_SIZE)
                Array.Clear(_sectorBuf, r, FatxConst.SECTOR_SIZE - r);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                long sector = _pos / FatxConst.SECTOR_SIZE;
                int inSector = (int)(_pos % FatxConst.SECTOR_SIZE);
                LoadSector(sector);
                int chunk = Math.Min(count - done, FatxConst.SECTOR_SIZE - inSector);
                Array.Copy(_sectorBuf, inSector, buffer, offset + done, chunk);
                _pos += chunk;
                done += chunk;
            }
            return done;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                long sector = _pos / FatxConst.SECTOR_SIZE;
                int inSector = (int)(_pos % FatxConst.SECTOR_SIZE);
                LoadSector(sector);
                int chunk = Math.Min(count - done, FatxConst.SECTOR_SIZE - inSector);
                Array.Copy(buffer, offset + done, _sectorBuf, inSector, chunk);
                _dirty = true;
                _pos += chunk;
                done += chunk;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin) _pos = offset;
            else if (origin == SeekOrigin.Current) _pos += offset;
            else _pos = _diskSize + offset;
            return _pos;
        }

        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { FlushSector(); _fs.Flush(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushSector();
                if (_fs != null) _fs.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    //  7. FATX FILE-SYSTEM ENGINE
    // ========================================================================

    /// <summary>Parsed FATX header.</summary>
    internal sealed class FatxHeader
    {
        public uint Magic;
        public uint VolumeId;
        public uint SectorsPerCluster;
        public uint RootCluster;
        public uint ClusterSize;   // derived
        public bool IsFat32;       // derived
        public int FatEntrySize;  // 2 or 4
        public long FatOffset;     // = HEADER_SIZE
        public long FatSize;       // aligned to page
        public long FileAreaOffset;
        public uint MaxClusters;

        public static FatxHeader Read(PartitionStream io, long partitionLength)
        {
            // БЫЛО:  io.GlobalOffset = 0;   // ← ломает offset для HDD
            // СТАЛО: не трогаем GlobalOffset — он уже выставлен в FatxPartition

            io.SeekTo(0);   // SeekTo сам прибавит GlobalOffset → попадём на HDD_DATA_OFFSET
            byte[] hdr = io.ReadBytes(FatxConst.HEADER_SIZE);

            FatxHeader h = new FatxHeader();
            h.Magic = Endian.ReadU32BE(hdr, 0);
            h.VolumeId = Endian.ReadU32BE(hdr, 4);
            h.SectorsPerCluster = Endian.ReadU32BE(hdr, 8);
            h.RootCluster = Endian.ReadU32BE(hdr, 12);

            if (h.Magic != FatxConst.MAGIC)
                throw new InvalidDataException(
                    string.Format("Bad FATX magic: 0x{0:X8} (expected 0x{1:X8}). " +
                                  "GlobalOffset=0x{2:X}, partitionLen=0x{3:X}",
                                  h.Magic, FatxConst.MAGIC,
                                  io.GlobalOffset, partitionLength));

            h.ClusterSize = h.SectorsPerCluster * FatxConst.SECTOR_SIZE;
            if (h.ClusterSize == 0) h.ClusterSize = 0x4000;

            h.MaxClusters = (uint)(partitionLength / h.ClusterSize) + 1;
            h.IsFat32 = h.MaxClusters >= FatxConst.FAT16_THRESHOLD;
            h.FatEntrySize = h.IsFat32 ? 4 : 2;

            h.FatOffset = FatxConst.HEADER_SIZE;
            long rawFat = (long)h.FatEntrySize * h.MaxClusters;
            h.FatSize = ((rawFat + 0xFFF) / 0x1000) * 0x1000;
            h.FileAreaOffset = h.FatOffset + h.FatSize;

            return h;
        }
    }

    /// <summary>A single directory entry (64 bytes).</summary>
    internal sealed class FatxDirent
    {
        public byte NameLength;
        public byte Attributes;
        public string Name;
        public uint FirstCluster;
        public uint FileSize;
        public uint CreationTime;
        public uint LastWriteTime;
        public uint LastAccessTime;

        public long Offset;      // absolute offset in partition stream
        public bool IsDeleted;
        public bool IsFree;
        public bool IsDirectory { get { return (Attributes & FatxConst.ATTR_DIRECTORY) != 0; } }
        public bool IsValid { get { return !IsFree && !IsDeleted && NameLength > 0 && NameLength <= FatxConst.MAX_FILENAME; } }

        public static FatxDirent Read(PartitionStream io, long offset)
        {
            io.SeekTo(offset);
            byte[] raw = io.ReadBytes(FatxConst.DIRENT_SIZE);
            if (raw.Length < FatxConst.DIRENT_SIZE) return null;

            FatxDirent d = new FatxDirent();
            d.Offset = offset;
            d.NameLength = raw[0];
            d.Attributes = raw[1];

            if (d.NameLength == FatxConst.DIRENT_NEVER_USED || d.NameLength == FatxConst.DIRENT_NEVER_USED2)
            {
                d.IsFree = true;
                d.Name = "";
                return d;
            }
            if (d.NameLength == FatxConst.DIRENT_DELETED)
            {
                d.IsDeleted = true;
            }

            int len = Math.Min((byte)d.NameLength, (byte)FatxConst.MAX_FILENAME);
            byte[] nameBytes = new byte[len];
            Array.Copy(raw, 2, nameBytes, 0, len);
            d.Name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', (char)0xFF);

            d.FirstCluster = Endian.ReadU32BE(raw, 0x2C);
            d.FileSize = Endian.ReadU32BE(raw, 0x30);
            d.CreationTime = Endian.ReadU32BE(raw, 0x34);
            d.LastWriteTime = Endian.ReadU32BE(raw, 0x38);
            d.LastAccessTime = Endian.ReadU32BE(raw, 0x3C);

            return d;
        }

        public void WriteTo(PartitionStream io)
        {
            byte[] raw = new byte[FatxConst.DIRENT_SIZE];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0xFF;

            raw[0] = NameLength;
            raw[1] = Attributes;
            byte[] nb = Encoding.ASCII.GetBytes(Name ?? "");
            Array.Copy(nb, 0, raw, 2, Math.Min(nb.Length, FatxConst.MAX_FILENAME));

            Endian.WriteU32BE(raw, 0x2C, FirstCluster);
            Endian.WriteU32BE(raw, 0x30, FileSize);
            Endian.WriteU32BE(raw, 0x34, CreationTime);
            Endian.WriteU32BE(raw, 0x38, LastWriteTime);
            Endian.WriteU32BE(raw, 0x3C, LastAccessTime);

            io.SeekTo(Offset);
            io.WriteBytes(raw);
        }

        public void MarkDeleted(PartitionStream io)
        {
            io.SeekTo(Offset);
            io.WriteBytes(new byte[] { FatxConst.DIRENT_DELETED });
        }

        public DateTime GetCreationDate()
        {
            return DosToDateTime(CreationTime);
        }
        public DateTime GetLastWriteDate()
        {
            return DosToDateTime(LastWriteTime);
        }

        /// <summary>
        /// uint32 BE = (DOS_date &lt;&lt; 16) | DOS_time.
        /// FAT/DOS time:  bits 15-11 = hour, 10-5 = min, 4-0 = sec/2  (per free60 FATX spec).
        /// FAT/DOS date:  bits 15-9 = year-1980, 8-5 = month, 4-0 = day.
        /// Возвращает DateTime.MinValue, если метка пустая (date==0 &amp;&amp; time==0).
        /// </summary>
        private static DateTime DosToDateTime(uint packed32)
        {
            uint date = (packed32 >> 16) & 0xFFFF;
            uint time = packed32 & 0xFFFF;
            if (date == 0 && time == 0) return DateTime.MinValue; // пусто → скроем в UI

            int day = (int)(date & 0x1F);
            int month = (int)((date >> 5) & 0x0F);
            int year = (int)((date >> 9) & 0x7F) + 1980;

            int sec2 = (int)(time & 0x1F);
            int min = (int)((time >> 5) & 0x3F);
            int hour = (int)((time >> 11) & 0x1F);

            // защита от мусорных бит (clamp в допустимые диапазоны)
            if (month < 1) month = 1; if (month > 12) month = 12;
            if (day < 1) day = 1;
            if (hour > 23) hour = 23;
            if (min > 59) min = 59;
            int sec = sec2 * 2; if (sec > 59) sec = 59;

            try
            {
                int daysInMonth = DateTime.DaysInMonth(year, month);
                if (day > daysInMonth) day = daysInMonth;
                return new DateTime(year, month, day, hour, min, sec);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static DateTime PackedTimeToDateTime(uint packed)
        {
            try
            {
                int year = (int)(packed & 0x7F) + 1980;
                int month = (int)((packed >> 7) & 0x0F);
                int day = (int)((packed >> 11) & 0x1F);
                int hour = (int)((packed >> 16) & 0x1F);
                int minute = (int)((packed >> 21) & 0x3F);
                int sec = (int)((packed >> 27) & 0x1F) * 2;
                if (month < 1) month = 1; if (month > 12) month = 12;
                if (day < 1) day = 1; if (day > 28) day = 28;
                if (hour > 23) hour = 0; if (minute > 59) minute = 0; if (sec > 59) sec = 0;
                return new DateTime(year, month, day, hour, minute, sec);
            }
            catch { return DateTime.MinValue; }
        }
    }

    /// <summary>FATX partition – the main file-system engine.</summary>
    internal sealed class FatxPartition : IDisposable
    {
        public PartitionStream IO;
        public FatxHeader Header;
        public string Label;
        private bool _disposed;

        public FatxPartition(PartitionStream io, long partitionOffset, long partitionLength, string label)
        {
            IO = io;
            IO.GlobalOffset = partitionOffset;
            Label = label;
            Header = FatxHeader.Read(io, partitionLength);
            Log.Ok(string.Format("FATX partition '{0}': cluster={1}, FAT{2}, clusters={3}",
                label, Header.ClusterSize, Header.IsFat32 ? "32" : "16", Header.MaxClusters));
        }

        // --- FAT helpers ---

        public long ClusterToOffset(uint cluster)
        {
            return Header.FileAreaOffset + (long)(cluster - 1) * Header.ClusterSize;
        }

        public uint ReadFatEntry(uint cluster)
        {
            long off = Header.FatOffset + (long)cluster * Header.FatEntrySize;
            IO.SeekTo(off);
            byte[] buf = IO.ReadBytes(Header.FatEntrySize);
            if (Header.IsFat32)
                return Endian.ReadU32BE(buf, 0);
            else
                return Endian.ReadU16BE(buf, 0);
        }

        public void WriteFatEntry(uint cluster, uint value)
        {
            long off = Header.FatOffset + (long)cluster * Header.FatEntrySize;
            IO.SeekTo(off);
            byte[] buf = new byte[Header.FatEntrySize];
            if (Header.IsFat32)
                Endian.WriteU32BE(buf, 0, value);
            else
                Endian.WriteU16BE(buf, 0, (ushort)value);
            IO.WriteBytes(buf);
        }

        /// <summary>Follow cluster chain from startCluster.</summary>
        public List<uint> GetClusterChain(uint startCluster)
        {
            List<uint> chain = new List<uint>();
            if (startCluster == 0) return chain;

            uint cur = startCluster;
            int guard = 0;
            int maxGuard = (int)(Header.MaxClusters + 10);
            while (cur != 0 && cur != FatxConst.CLUSTER_LAST && cur != FatxConst.CLUSTER_LAST16)
            {
                if (cur >= Header.MaxClusters)
                {
                    Log.Warn("Cluster chain out of range: " + cur);
                    break;
                }
                chain.Add(cur);
                uint next = ReadFatEntry(cur);
                if (next == cur) break; // infinite loop guard
                cur = next;
                if (++guard > maxGuard)
                {
                    Log.Warn("Cluster chain too long, breaking.");
                    break;
                }
            }
            return chain;
        }

        // --- Directory reading ---

        /// <summary>Read all dirents from a cluster chain.</summary>
        public List<FatxDirent> ReadDirectory(uint startCluster)
        {
            List<FatxDirent> entries = new List<FatxDirent>();
            List<uint> chain = GetClusterChain(startCluster);
            int direntsPerCluster = (int)(Header.ClusterSize / FatxConst.DIRENT_SIZE);

            foreach (uint cl in chain)
            {
                long baseOff = ClusterToOffset(cl);
                for (int i = 0; i < direntsPerCluster; i++)
                {
                    long off = baseOff + (long)i * FatxConst.DIRENT_SIZE;
                    try
                    {
                        FatxDirent d = FatxDirent.Read(IO, off);
                        if (d == null) continue;
                        if (d.IsFree && d.NameLength == FatxConst.DIRENT_NEVER_USED2)
                            return entries; // end of directory
                        entries.Add(d);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Dirent read error at offset " + off + ": " + ex.Message);
                    }
                }
            }
            return entries;
        }

        // --- File extraction (large-file safe, buffered) ---

        public void ExtractFile(FatxDirent dirent, string destPath, IProgressReporter progress)
        {
            if (dirent.IsDirectory)
            {
                ExtractDirectoryRecursive(dirent, destPath, progress);
                return;
            }

            List<uint> chain = GetClusterChain(dirent.FirstCluster);
            long totalSize = dirent.FileSize;
            long written = 0;

            using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, FatxConst.IO_BUFFER_SIZE))
            {
                for (int ci = 0; ci < chain.Count; ci++)
                {
                    long off = ClusterToOffset(chain[ci]);
                    IO.SeekTo(off);

                    int toRead = (int)Math.Min(Header.ClusterSize, totalSize - written);
                    if (toRead <= 0) break;

                    byte[] buf = new byte[Math.Min(toRead, FatxConst.IO_BUFFER_SIZE)];
                    int remaining = toRead;
                    while (remaining > 0)
                    {
                        int chunk = Math.Min(remaining, buf.Length);
                        int got = IO.Read(buf, 0, chunk);
                        if (got <= 0) break;
                        fs.Write(buf, 0, got);
                        remaining -= got;
                        written += got;
                    }

                    if (progress != null && totalSize > 0)
                        progress.Report((int)(written * 100 / totalSize), written, totalSize);
                }
            }
            Log.Ok("Extracted: " + dirent.Name + " → " + destPath);
        }

        public void ExtractDirectoryRecursive(FatxDirent dirDirent, string destDir, IProgressReporter progress)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            List<FatxDirent> entries = ReadDirectory(dirDirent.FirstCluster);
            foreach (FatxDirent e in entries)
            {
                if (!e.IsValid) continue;
                string sub = Path.Combine(destDir, SanitizeName(e.Name));
                if (e.IsDirectory)
                    ExtractDirectoryRecursive(e, sub, progress);
                else
                    ExtractFile(e, sub, progress);
            }
        }

        // --- File injection ---

        public uint FindFreeCluster(uint startFrom)
        {
            for (uint c = startFrom; c < Header.MaxClusters; c++)
            {
                if (ReadFatEntry(c) == FatxConst.CLUSTER_FREE)
                    return c;
            }
            throw new IOException("No free clusters available.");
        }

        public uint[] AllocateChain(int count)
        {
            uint[] chain = new uint[count];
            uint search = 2;
            for (int i = 0; i < count; i++)
            {
                chain[i] = FindFreeCluster(search);
                search = chain[i] + 1;
            }
            // link
            for (int i = 0; i < count - 1; i++)
                WriteFatEntry(chain[i], chain[i + 1]);
            WriteFatEntry(chain[count - 1], FatxConst.CLUSTER_LAST);
            return chain;
        }

        public void FreeChain(uint startCluster)
        {
            List<uint> chain = GetClusterChain(startCluster);
            foreach (uint c in chain)
                WriteFatEntry(c, FatxConst.CLUSTER_FREE);
        }

        public void InjectFile(string srcPath, uint parentCluster, IProgressReporter progress)
        {
            string fileName = Path.GetFileName(srcPath);
            if (fileName.Length > FatxConst.MAX_FILENAME)
                fileName = fileName.Substring(0, FatxConst.MAX_FILENAME);

            // check duplicate
            List<FatxDirent> existing = ReadDirectory(parentCluster);
            foreach (FatxDirent e in existing)
            {
                if (e.IsValid && string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("File already exists: " + fileName);
            }

            FileInfo fi = new FileInfo(srcPath);
            long fileSize = fi.Length;
            int clustersNeeded = (int)((fileSize + Header.ClusterSize - 1) / Header.ClusterSize);
            if (clustersNeeded == 0) clustersNeeded = 1;

            uint[] chain = AllocateChain(clustersNeeded);

            // write data
            using (FileStream fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, FatxConst.IO_BUFFER_SIZE))
            {
                long written = 0;
                for (int ci = 0; ci < chain.Length; ci++)
                {
                    long off = ClusterToOffset(chain[ci]);
                    IO.SeekTo(off);
                    int toWrite = (int)Math.Min(Header.ClusterSize, fileSize - written);
                    if (toWrite <= 0)
                    {
                        // zero-fill remaining
                        IO.WriteBytes(new byte[Header.ClusterSize]);
                        continue;
                    }
                    byte[] buf = new byte[Header.ClusterSize];
                    int got = fs.Read(buf, 0, toWrite);
                    IO.WriteBytes(buf);
                    written += got;
                    if (progress != null && fileSize > 0)
                        progress.Report((int)(written * 100 / fileSize), written, fileSize);
                }
            }

            // find free dirent slot
            long direntOff = FindFreeDirentOffset(parentCluster);
            FatxDirent nd = new FatxDirent();
            nd.Offset = direntOff;
            nd.NameLength = (byte)fileName.Length;
            nd.Attributes = FatxConst.ATTR_ARCHIVE;
            nd.Name = fileName;
            nd.FirstCluster = chain[0];
            nd.FileSize = (uint)fileSize;
            nd.CreationTime = DateTimeToPacked(DateTime.Now);
            nd.LastWriteTime = nd.CreationTime;
            nd.LastAccessTime = nd.CreationTime;
            nd.WriteTo(IO);
            IO.Flush();

            Log.Ok("Injected: " + fileName + " (" + fileSize + " bytes, " + clustersNeeded + " clusters)");
        }

        public void InjectFolder(string srcDir, uint parentCluster, IProgressReporter progress)
        {
            string folderName = Path.GetFileName(srcDir);
            if (folderName.Length > FatxConst.MAX_FILENAME)
                folderName = folderName.Substring(0, FatxConst.MAX_FILENAME);

            // create directory cluster
            uint dirCluster = FindFreeCluster(2);
            WriteFatEntry(dirCluster, FatxConst.CLUSTER_LAST);

            // zero-fill the cluster with 0xFF
            IO.SeekTo(ClusterToOffset(dirCluster));
            byte[] fill = new byte[Header.ClusterSize];
            for (int i = 0; i < fill.Length; i++) fill[i] = 0xFF;
            IO.WriteBytes(fill);

            // write dirent for the folder
            long direntOff = FindFreeDirentOffset(parentCluster);
            FatxDirent nd = new FatxDirent();
            nd.Offset = direntOff;
            nd.NameLength = (byte)folderName.Length;
            nd.Attributes = FatxConst.ATTR_DIRECTORY;
            nd.Name = folderName;
            nd.FirstCluster = dirCluster;
            nd.FileSize = 0;
            nd.CreationTime = DateTimeToPacked(DateTime.Now);
            nd.LastWriteTime = nd.CreationTime;
            nd.LastAccessTime = nd.CreationTime;
            nd.WriteTo(IO);

            // inject contents
            foreach (string f in Directory.GetFiles(srcDir))
                InjectFile(f, dirCluster, progress);
            foreach (string d in Directory.GetDirectories(srcDir))
                InjectFolder(d, dirCluster, progress);

            IO.Flush();
        }

        public long FindFreeDirentOffset(uint dirCluster)
        {
            List<uint> chain = GetClusterChain(dirCluster);
            int dpc = (int)(Header.ClusterSize / FatxConst.DIRENT_SIZE);
            foreach (uint cl in chain)
            {
                long baseOff = ClusterToOffset(cl);
                for (int i = 0; i < dpc; i++)
                {
                    long off = baseOff + (long)i * FatxConst.DIRENT_SIZE;
                    IO.SeekTo(off);
                    byte b = IO.ReadBytes(1)[0];
                    if (b == FatxConst.DIRENT_NEVER_USED || b == FatxConst.DIRENT_NEVER_USED2 || b == FatxConst.DIRENT_DELETED)
                        return off;
                }
            }
            // need to extend directory – allocate new cluster
            uint newCl = FindFreeCluster(2);
            WriteFatEntry(chain[chain.Count - 1], newCl);
            WriteFatEntry(newCl, FatxConst.CLUSTER_LAST);
            IO.SeekTo(ClusterToOffset(newCl));
            byte[] fill = new byte[Header.ClusterSize];
            for (int i = 0; i < fill.Length; i++) fill[i] = 0xFF;
            IO.WriteBytes(fill);
            return ClusterToOffset(newCl);
        }

        public void DeleteDirent(FatxDirent d)
        {
            if (!d.IsDirectory && d.FirstCluster != 0)
                FreeChain(d.FirstCluster);
            d.MarkDeleted(IO);
            IO.Flush();
            Log.Ok("Deleted: " + d.Name);
        }

        public void RenameDirent(FatxDirent d, string newName)
        {
            if (newName.Length > FatxConst.MAX_FILENAME)
                newName = newName.Substring(0, FatxConst.MAX_FILENAME);
            d.Name = newName;
            d.NameLength = (byte)newName.Length;
            d.WriteTo(IO);
            IO.Flush();
            Log.Ok("Renamed to: " + newName);
        }

        public void CreateFolder(string name, uint parentCluster)
        {
            uint dirCluster = FindFreeCluster(2);
            WriteFatEntry(dirCluster, FatxConst.CLUSTER_LAST);
            IO.SeekTo(ClusterToOffset(dirCluster));
            byte[] fill = new byte[Header.ClusterSize];
            for (int i = 0; i < fill.Length; i++) fill[i] = 0xFF;
            IO.WriteBytes(fill);

            long direntOff = FindFreeDirentOffset(parentCluster);
            FatxDirent nd = new FatxDirent();
            nd.Offset = direntOff;
            nd.NameLength = (byte)name.Length;
            nd.Attributes = FatxConst.ATTR_DIRECTORY;
            nd.Name = name;
            nd.FirstCluster = dirCluster;
            nd.FileSize = 0;
            nd.CreationTime = DateTimeToPacked(DateTime.Now);
            nd.LastWriteTime = nd.CreationTime;
            nd.LastAccessTime = nd.CreationTime;
            nd.WriteTo(IO);
            IO.Flush();
            Log.Ok("Created folder: " + name);
        }

        // --- helpers ---

        public static uint DateTimeToPacked(DateTime dt)
        {
            uint date = 0;
            date |= (uint)(((dt.Year - 1980) & 0x7F) << 9);
            date |= (uint)((dt.Month & 0x0F) << 5);
            date |= (uint)(dt.Day & 0x1F);

            uint time = 0;
            time |= (uint)((dt.Hour & 0x1F) << 11);
            time |= (uint)((dt.Minute & 0x3F) << 5);
            time |= (uint)((dt.Second / 2) & 0x1F);

            return (date << 16) | time;   // BE-порядок: date в старших 16 битах
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (IO != null) IO.Dispose();
        }
    }

    // ========================================================================
    //  8. PROGRESS REPORTER INTERFACE
    // ========================================================================
    internal interface IProgressReporter
    {
        void Report(int percent, long done, long total);
    }

    // ========================================================================
    //  9. USB / HDD DETECTION HELPERS
    // ========================================================================
    internal static class DeviceDetect
    {
        /// <summary>Auto-detect Xbox360 folder on removable drives.</summary>
        public static string FindXbox360Folder()
        {
            try
            {
                foreach (string drive in Directory.GetLogicalDrives())
                {
                    if (drive.StartsWith("A:", StringComparison.OrdinalIgnoreCase)) continue;
                    string xbox = Path.Combine(drive, "Xbox360");
                    if (Directory.Exists(xbox)) return xbox;
                }
            }
            catch (Exception ex) { Log.Debug("FindXbox360Folder: " + ex.Message); }
            return null;
        }

        /// <summary>Find Data0000 (cache) in Xbox360 folder.</summary>
        public static string FindCacheFile(string xbox360Path)
        {
            string p = Path.Combine(xbox360Path, "Data0000");
            return File.Exists(p) ? p : null;
        }

        /// <summary>Find Data0001, Data0002, … (data partition files).</summary>
        public static string[] FindDataFiles(string xbox360Path)
        {
            List<string> list = new List<string>();
            for (int i = 1; i <= 9999; i++)
            {
                string name = "Data" + i.ToString("D4");
                string p = Path.Combine(xbox360Path, name);
                if (File.Exists(p)) list.Add(p);
                else break;
            }
            return list.ToArray();
        }

        /// <summary>Scan physical drives 0..15 for XTAF magic at HDD data offset.</summary>
        public static int FindXboxHdd()
        {
            for (int i = 0; i < 16; i++)
            {
                try
                {
                    using (RawDiskStream rds = new RawDiskStream(i))
                    {
                        rds.Seek(FatxConst.HDD_DATA_OFFSET, SeekOrigin.Begin);
                        byte[] magic = new byte[4];
                        rds.Read(magic, 0, 4);
                        if (magic[0] == (byte)'X' && magic[1] == (byte)'T' &&
                            magic[2] == (byte)'A' && magic[3] == (byte)'F')
                        {
                            Log.Ok("Xbox 360 HDD found at PhysicalDrive" + i);
                            return i;
                        }
                    }
                }
                catch { }
            }
            return -1;
        }
    }

    // ========================================================================
    //  10. CONSOLE TUI HELPERS  (progress bar, coloured status)
    // ========================================================================
    internal static class Tui
    {
        public static void SetTitle(string status)
        {
            try { Console.Title = "Xbox 360 USB Explorer — " + status; } catch { }
        }

        public static void DrawProgressBar(int percent, int width, string label)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            int filled = width * percent / 100;
            StringBuilder sb = new StringBuilder();
            sb.Append("  [");
            for (int i = 0; i < width; i++)
                sb.Append(i < filled ? '█' : '░');
            sb.Append("] ");
            sb.Append(percent.ToString().PadLeft(3));
            sb.Append("% ");
            sb.Append(label ?? "");
            Console.Write("\r" + sb.ToString());
            if (percent >= 100) Console.WriteLine();
        }

        public static void Header(string text)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  ╔" + new string('═', text.Length + 2) + "╗");
            Console.WriteLine("  ║ " + text + " ║");
            Console.WriteLine("  ╚" + new string('═', text.Length + 2) + "╝");
            Console.ResetColor();
        }

        public static int ShowMenu(string title, string[] options)
        {
            int sel = 0;
            ConsoleKey key;
            do
            {
                Console.Clear();
                Header(title);
                Console.WriteLine();
                for (int i = 0; i < options.Length; i++)
                {
                    if (i == sel)
                    {
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.WriteLine("  ► " + options[i]);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine("    " + options[i]);
                    }
                }
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  ↑↓ select · Enter confirm · Esc back");
                Console.ResetColor();

                key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.UpArrow && sel > 0) sel--;
                else if (key == ConsoleKey.DownArrow && sel < options.Length - 1) sel++;
            } while (key != ConsoleKey.Enter && key != ConsoleKey.Escape);

            return key == ConsoleKey.Escape ? -1 : sel;
        }

        public static string Prompt(string label)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("  " + label + ": ");
            Console.ResetColor();
            return Console.ReadLine();
        }
    }

    /// <summary>Console-based progress reporter.</summary>
    internal sealed class ConsoleProgress : IProgressReporter
    {
        private string _label;
        public ConsoleProgress(string label) { _label = label; }
        public void Report(int percent, long done, long total)
        {
            Tui.DrawProgressBar(percent, 40,
                _label + " " + FormatSize(done) + " / " + FormatSize(total));
        }
        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }

    // ========================================================================
    //  11. WINFORMS GUI
    // ========================================================================

    /// <summary>GUI progress reporter (marshals to UI thread).</summary>
    internal sealed class GuiProgress : IProgressReporter
    {
        private ProgressBar _bar;
        private ToolStripStatusLabel _label;
        public GuiProgress(ProgressBar bar, ToolStripStatusLabel label)
        {
            _bar = bar;
            _label = label;
        }
        public void Report(int percent, long done, long total)
        {
            if (_bar.InvokeRequired)
            {
                _bar.BeginInvoke((MethodInvoker)delegate { SetVal(percent, done, total); });
            }
            else SetVal(percent, done, total);
        }
        private void SetVal(int p, long d, long t)
        {
            _bar.Value = Math.Max(0, Math.Min(100, p));
            _label.Text = ConsoleProgress.FormatSize(d) + " / " + ConsoleProgress.FormatSize(t);
        }
    }

    // ---- Main Form ----
    internal sealed class MainForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);   // после этого _split.Width уже реальный (>= MinimumSize.Width)

            // 1) СНАЧАЛА позиция разделителя (при дефолтных MinSize 25/25 это всегда легально)
            try
            {
                int target = 210;
                int lo = _split.Panel1MinSize;
                int hi = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
                if (target < lo) target = lo;
                if (target > hi) target = hi;
                _split.SplitterDistance = target;
            }
            catch (Exception ex) { Log.Debug("SplitterDistance: " + ex.Message); }

            // 2) ПОТОМ мин. ширину левой (теперь SplitterDistance=210 >= 120 → ок)
            try { _split.Panel1MinSize = 120; }
            catch (Exception ex) { Log.Debug("Panel1MinSize: " + ex.Message); }

            // 3) И ТОЛЬКО ПОТОМ мин. ширину правой (Width-210-6 >= 200 при Width>=416 → ок)
            try { _split.Panel2MinSize = 200; }
            catch (Exception ex) { Log.Debug("Panel2MinSize: " + ex.Message); }
        }

        private TreeView _tree;
        private ListView _list;
        private SplitContainer _split;
        private MenuStrip _menu;
        private StatusStrip _status;
        private ToolStripStatusLabel _statusLabel;
        private ProgressBar _progressBar;
        private ImageList _images;
        private ContextMenuStrip _ctxList;
        private ContextMenuStrip _ctxTree;

        private FatxPartition _dataPart;
        private FatxPartition _cachePart;
        private FatxPartition _compatPart;
        private bool _deviceOpen;

        private FatxPartition _curPart;          // что сейчас показано в правом списке
        private uint _curCluster;       // кластер текущей папки списка
        private readonly Stack<Nav> _backStack = new Stack<Nav>();   // история для ".."
        private TreeNode _syncedTreeNode;   // нода дерева, синхронная со списком
        private bool _suppressTreeSelect;

        private struct Nav
        {
            public FatxPartition Part; public uint Cluster;
            public Nav(FatxPartition p, uint c) { Part = p; Cluster = c; }
        }
        private sealed class UpTag { }           // маркер строки ".."

        // Tag helpers
        private sealed class NodeTag
        {
            public FatxPartition Partition;
            public uint Cluster;
            public string Name;
            public NodeTag(FatxPartition p, uint c, string n) { Partition = p; Cluster = c; Name = n; }
        }
        private sealed class ItemTag
        {
            public FatxDirent Dirent;
            public FatxPartition Partition;
            public ItemTag(FatxDirent d, FatxPartition p) { Dirent = d; Partition = p; }
        }

        public MainForm()
        {
            BuildUi();
            Tui.SetTitle("Ready");
            _statusLabel.Text = "Open a device to begin.";
        }

        private void BuildUi()
        {
            Text = "Xbox 360 USB Explorer v3.0";
            MinimumSize = new Size(860, 540);
            StartPosition = FormStartPosition.CenterScreen;

            // --- Image list (code-generated icons) ---
            _images = new ImageList();
            _images.ImageSize = new Size(16, 16);
            _images.Images.Add("folder", MakeFolderIcon());
            _images.Images.Add("file", MakeFileIcon());
            _images.Images.Add("con", MakeConIcon());
            _images.Images.Add("up", MakeUpIcon());

            // --- Menu ---
            _menu = new MenuStrip();

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add("Open &USB Drive", null, OnOpenUsb);
            fileMenu.DropDownItems.Add("Open USB &Manually…", null, OnOpenUsbManual);
            fileMenu.DropDownItems.Add("Open HDD &Image…", null, OnOpenHddImage);
            ToolStripMenuItem expMenu = new ToolStripMenuItem("E&xperimental");
            expMenu.DropDownItems.Add("Open Raw &HDD (Admin)", null, OnOpenRawHdd);
            fileMenu.DropDownItems.Add(expMenu);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("&Close Device", null, OnCloseDevice);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, delegate { Close(); });

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
            helpMenu.DropDownItems.Add("&About", null, delegate { new AboutForm().ShowDialog(this); });

            _menu.Items.Add(fileMenu);
            _menu.Items.Add(helpMenu);

            // --- Tree ---
            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.ImageList = _images;
            _tree.ImageIndex = 0;
            _tree.SelectedImageIndex = 0;
            _tree.AfterSelect += OnTreeSelect;
            _tree.HideSelection = false;

            _ctxTree = new ContextMenuStrip();
            _ctxTree.Items.Add("Extract…", null, OnTreeExtract);
            _ctxTree.Items.Add("Inject File…", null, OnTreeInjectFile);
            _ctxTree.Items.Add("Inject Folder…", null, OnTreeInjectFolder);
            _ctxTree.Items.Add("New Folder…", null, OnTreeNewFolder);
            _ctxTree.Items.Add("Delete", null, OnTreeDelete);
            _tree.ContextMenuStrip = _ctxTree;

            // --- List ---
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.SmallImageList = _images;
            _list.AllowDrop = true;
            _list.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            _list.DragDrop += OnListDragDrop;
            _list.Columns.Add("Name", 220);
            _list.Columns.Add("Size", 100);
            _list.Columns.Add("Type", 80);
            _list.Columns.Add("Modified", 140);

            _ctxList = new ContextMenuStrip();
            _ctxList.Items.Add("Extract…", null, OnListExtract);
            _ctxList.Items.Add("Delete", null, OnListDelete);
            _ctxList.Items.Add("Rename…", null, OnListRename);
            _ctxList.Items.Add(new ToolStripSeparator());
            _ctxList.Items.Add("Inject File…", null, OnTreeInjectFile);
            _ctxList.Items.Add("Inject Folder…", null, OnTreeInjectFolder);
            _ctxList.Items.Add("New Folder…", null, OnTreeNewFolder);
            _list.ContextMenuStrip = _ctxList;

            _list.Activation = ItemActivation.Standard;   // Enter и dbl-click → ItemActivate
            _list.ItemActivate += OnListActivate;

            // --- Split ---
            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Panel1.Controls.Add(_tree);
            _split.Panel2.Controls.Add(_list);

            _split.FixedPanel = FixedPanel.Panel1;   // левая (дерево) не тянется при ресайзе
            _split.SplitterWidth = 6;
            // ⚠ MinSize и SplitterDistance НЕ ТРОГАЕМ здесь — Width ещё 150px, будет взрыв.
            //    Всё это ставится в OnLoad (см. сниппет 2).

            // --- Status ---
            _status = new StatusStrip();
            _progressBar = new ProgressBar();
            _progressBar.Size = new Size(150, 16);
            _progressBar.Visible = false;
            _statusLabel = new ToolStripStatusLabel("Ready");
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _status.Items.Add(_statusLabel);
            _status.Items.Add(new ToolStripControlHost(_progressBar));

            // --- Layout ---
            Controls.Add(_split);
            Controls.Add(_status);
            Controls.Add(_menu);
            MainMenuStrip = _menu;
        }

        // ---- Code-generated icons (no Resources!) ----
        private static Bitmap MakeFolderIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.Gold, 1, 4, 14, 10);
                g.FillRectangle(Brushes.Gold, 1, 2, 6, 3);
                g.DrawRectangle(Pens.DarkGoldenrod, 1, 4, 13, 9);
            }
            return b;
        }
        private static Bitmap MakeUpIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.Khaki, 1, 5, 14, 9);
                g.FillRectangle(Brushes.Khaki, 1, 3, 6, 3);
                g.DrawRectangle(Pens.DarkGoldenrod, 1, 5, 13, 8);
                using (Pen p = new Pen(Color.DarkSlateBlue, 2))
                {
                    g.DrawLine(p, 8, 11, 8, 6);          // стрелка вверх
                    g.DrawLine(p, 8, 6, 5, 9);
                    g.DrawLine(p, 8, 6, 11, 9);
                }
            }
            return b;
        }

        private static Bitmap MakeFileIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.White, 3, 1, 10, 14);
                g.DrawRectangle(Pens.Gray, 3, 1, 9, 13);
                g.DrawLine(Pens.Gray, 10, 1, 13, 4);
            }
            return b;
        }
        private static Bitmap MakeConIcon()
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.LightSteelBlue, 3, 1, 10, 14);
                g.DrawRectangle(Pens.SteelBlue, 3, 1, 9, 13);
                g.DrawString("C", new Font("Arial", 7, FontStyle.Bold), Brushes.DarkBlue, 4, 3);
            }
            return b;
        }

        // ---- Device open handlers ----

        private void OnOpenUsb(object sender, EventArgs e)
        {
            if (_deviceOpen) { Warn("Close current device first."); return; }
            string xbox = DeviceDetect.FindXbox360Folder();
            if (xbox == null) { Warn("No Xbox360 folder found. Try opening manually."); return; }
            OpenUsbPath(xbox);
        }

        private void OnOpenUsbManual(object sender, EventArgs e)
        {
            if (_deviceOpen) { Warn("Close current device first."); return; }
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select the Xbox360 folder on your USB drive";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                string path = fbd.SelectedPath;
                if (!path.EndsWith("Xbox360", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = Path.Combine(path, "Xbox360");
                    if (Directory.Exists(sub)) path = sub;
                }
                OpenUsbPath(path);
            }
        }

        private void OpenUsbPath(string xbox360Path)
        {
            try
            {
                Tui.SetTitle("Opening USB…");
                _statusLabel.Text = "Opening USB drive…";

                string cacheFile = DeviceDetect.FindCacheFile(xbox360Path);
                string[] dataFiles = DeviceDetect.FindDataFiles(xbox360Path);

                if (dataFiles.Length == 0)
                {
                    Warn("No Data files found in " + xbox360Path);
                    return;
                }

                Log.Info("USB Xbox360 path: " + xbox360Path);
                Log.Info("Cache: " + (cacheFile ?? "none"));
                Log.Info("Data files: " + dataFiles.Length);

                // Data partition
                PartitionStream dataIo = new PartitionStream(dataFiles);
                long dataLen = 0;
                foreach (string df in dataFiles)
                    dataLen += new FileInfo(df).Length;
                _dataPart = new FatxPartition(dataIo, 0, dataLen, "Data");

                // Cache partition (optional)
                if (cacheFile != null)
                {
                    try
                    {
                        PartitionStream cacheIo = new PartitionStream(cacheFile);
                        long cacheLen = new FileInfo(cacheFile).Length;
                        _cachePart = new FatxPartition(cacheIo, FatxConst.USB_CACHE_OFFSET,
                            cacheLen - FatxConst.USB_CACHE_OFFSET, "Cache");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Cache partition error: " + ex.Message);
                        _cachePart = null;
                    }
                }

                BuildTree();
                _deviceOpen = true;
                _statusLabel.Text = "USB drive opened. Data files: " + dataFiles.Length;
                Tui.SetTitle("USB Open");
                Log.Ok("USB device opened successfully.");
            }
            catch (Exception ex)
            {
                Log.Error("OpenUsbPath: " + ex.Message);
                Warn("Failed to open USB: " + ex.Message);
                ClosePartitions();
            }
        }

        private void OnOpenHddImage(object sender, EventArgs e)
        {
            if (_deviceOpen) { Warn("Close current device first."); return; }
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Open Xbox 360 HDD Image";
                ofd.Filter = "All files (*.*)|*.*";
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Tui.SetTitle("Opening HDD image…");
                    _statusLabel.Text = "Opening HDD image…";
                    string path = ofd.FileName;
                    long fileLen = new FileInfo(path).Length;

                    PartitionStream io = new PartitionStream(path);
                    long dataPartLen = fileLen - FatxConst.HDD_DATA_OFFSET;
                    if (dataPartLen <= 0)
                    {
                        Warn("Image too small to contain HDD data partition.");
                        return;
                    }
                    _dataPart = new FatxPartition(io, FatxConst.HDD_DATA_OFFSET, dataPartLen, "Data (HDD)");

                    // Try compat
                    try
                    {
                        PartitionStream io2 = new PartitionStream(path);
                        _compatPart = new FatxPartition(io2, FatxConst.HDD_COMPAT_OFFSET,
                            FatxConst.HDD_COMPAT_SIZE, "Compatibility");
                    }
                    catch { _compatPart = null; }

                    // Cache (HDD image) — пробуем, тихо пропускаем если нет
                    if (fileLen > FatxConst.HDD_CACHE_OFFSET + FatxConst.HEADER_SIZE)
                    {
                        try
                        {
                            PartitionStream ioC = new PartitionStream(path);
                            long lenC = Math.Min((long)FatxConst.HDD_CACHE_SIZE, fileLen - FatxConst.HDD_CACHE_OFFSET);
                            _cachePart = new FatxPartition(ioC, FatxConst.HDD_CACHE_OFFSET, lenC, "Cache (HDD)");
                        }
                        catch (Exception ex) { Log.Warn("Cache partition skipped: " + ex.Message); _cachePart = null; }
                    }

                    BuildTree();
                    _deviceOpen = true;
                    _statusLabel.Text = "HDD image opened: " + Path.GetFileName(path);
                    Tui.SetTitle("HDD Image Open");
                    Log.Ok("HDD image opened.");
                }
                catch (Exception ex)
                {
                    Log.Error("OnOpenHddImage: " + ex.Message);
                    Warn("Failed: " + ex.Message);
                    ClosePartitions();
                }
            }
        }

        private void OnOpenRawHdd(object sender, EventArgs e)
        {
            if (_deviceOpen) { Warn("Close current device first."); return; }
            if (!Win32Native.IsAdministrator())
            {
                Log.Warn("Raw HDD access requires Administrator. Elevating…");
                Win32Native.ElevateAndRestart(new string[] { "--gui" });
                return;
            }

            try
            {
                Tui.SetTitle("Scanning for Xbox HDD…");
                _statusLabel.Text = "Scanning physical drives…";
                int idx = DeviceDetect.FindXboxHdd();
                if (idx < 0) { Warn("No Xbox 360 HDD found."); return; }

                long diskLen = 0;
                using (RawDiskStream probe = new RawDiskStream(idx)) diskLen = probe.Length;
                if (diskLen <= FatxConst.HDD_DATA_OFFSET)
                { Warn("Cannot determine disk size / disk too small."); return; }
                Log.Info(string.Format("Raw HDD {0}: diskLen=0x{1:X}", idx, diskLen));

                // DATA (обязательный)
                try
                {
                    PartitionStream io = new PartitionStream(new RawDiskStream(idx));
                    io.LengthOverride = diskLen;
                    _dataPart = new FatxPartition(io, FatxConst.HDD_DATA_OFFSET,
                        diskLen - FatxConst.HDD_DATA_OFFSET, "Data (Raw HDD)");
                }
                catch (Exception ex) { Log.Error("Data partition: " + ex.Message); _dataPart = null; }

                // CACHE (пробуем — если региона нет / не FATX, тихо пропускаем)
                if (diskLen > FatxConst.HDD_CACHE_OFFSET + FatxConst.HEADER_SIZE)
                {
                    try
                    {
                        PartitionStream io = new PartitionStream(new RawDiskStream(idx));
                        io.LengthOverride = diskLen;
                        long len = Math.Min((long)FatxConst.HDD_CACHE_SIZE, diskLen - FatxConst.HDD_CACHE_OFFSET);
                        _cachePart = new FatxPartition(io, FatxConst.HDD_CACHE_OFFSET, len, "Cache (Raw HDD)");
                    }
                    catch (Exception ex) { Log.Warn("Cache partition skipped: " + ex.Message); _cachePart = null; }
                }
                else Log.Info("Cache region out of disk range — skipped.");

                // COMPAT (пробуем)
                if (diskLen > FatxConst.HDD_COMPAT_OFFSET + FatxConst.HEADER_SIZE)
                {
                    try
                    {
                        PartitionStream io = new PartitionStream(new RawDiskStream(idx));
                        io.LengthOverride = diskLen;
                        long len = Math.Min((long)FatxConst.HDD_COMPAT_SIZE, diskLen - FatxConst.HDD_COMPAT_OFFSET);
                        _compatPart = new FatxPartition(io, FatxConst.HDD_COMPAT_OFFSET, len, "Compatibility (Raw HDD)");
                    }
                    catch (Exception ex) { Log.Warn("Compat partition skipped: " + ex.Message); _compatPart = null; }
                }
                else Log.Info("Compat region out of disk range — skipped.");

                if (_dataPart == null && _cachePart == null && _compatPart == null)
                { Warn("No valid FATX partitions found on this HDD."); return; }

                BuildTree();
                _deviceOpen = true;
                _statusLabel.Text = "Raw HDD " + idx + " opened.";
                Tui.SetTitle("Raw HDD " + idx);
                Log.Ok(string.Format("Raw HDD opened. Partitions: Data={0} Cache={1} Compat={2}",
                    _dataPart != null, _cachePart != null, _compatPart != null));
            }
            catch (Exception ex)
            {
                Log.Error("OnOpenRawHdd: " + ex.Message);
                Warn("Failed: " + ex.Message);
                ClosePartitions();
            }
        }

        private void OnCloseDevice(object sender, EventArgs e)
        {
            ClosePartitions();
            _tree.Nodes.Clear();
            _list.Items.Clear();
            _deviceOpen = false;
            _statusLabel.Text = "Device closed.";
            Tui.SetTitle("Ready");
        }

        private void ClosePartitions()
        {
            try { if (_dataPart != null) _dataPart.Dispose(); } catch { }
            try { if (_cachePart != null) _cachePart.Dispose(); } catch { }
            try { if (_compatPart != null) _compatPart.Dispose(); } catch { }
            _dataPart = null;
            _cachePart = null;
            _compatPart = null;
        }

        // ---- Tree building ----

        private void BuildTree()
        {
            _tree.Nodes.Clear();
            _list.Items.Clear();

            if (_dataPart != null)
            {
                TreeNode dataNode = new TreeNode("Data Partition");
                dataNode.Tag = new NodeTag(_dataPart, _dataPart.Header.RootCluster, "Data");
                LoadSubDirs(dataNode, _dataPart, _dataPart.Header.RootCluster);
                _tree.Nodes.Add(dataNode);
            }
            if (_cachePart != null)
            {
                TreeNode cacheNode = new TreeNode("Cache Partition");
                cacheNode.Tag = new NodeTag(_cachePart, _cachePart.Header.RootCluster, "Cache");
                LoadSubDirs(cacheNode, _cachePart, _cachePart.Header.RootCluster);
                _tree.Nodes.Add(cacheNode);
            }
            if (_compatPart != null)
            {
                TreeNode compatNode = new TreeNode("Compatibility Partition");
                compatNode.Tag = new NodeTag(_compatPart, _compatPart.Header.RootCluster, "Compat");
                LoadSubDirs(compatNode, _compatPart, _compatPart.Header.RootCluster);
                _tree.Nodes.Add(compatNode);
            }

            if (_tree.Nodes.Count > 0)
                _tree.SelectedNode = _tree.Nodes[0];
        }

        private void LoadSubDirs(TreeNode parent, FatxPartition part, uint cluster)
        {
            try
            {
                List<FatxDirent> entries = part.ReadDirectory(cluster);
                foreach (FatxDirent e in entries)
                {
                    if (!e.IsValid || !e.IsDirectory) continue;
                    TreeNode child = new TreeNode(e.Name);
                    child.Tag = new NodeTag(part, e.FirstCluster, e.Name);
                    child.ImageIndex = 0;
                    child.SelectedImageIndex = 0;
                    // lazy-load: add placeholder
                    child.Nodes.Add("__loading__");
                    parent.Nodes.Add(child);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("LoadSubDirs error: " + ex.Message);
            }
        }

        private void OnTreeSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressTreeSelect) return;                 // программное выделение — игнор
            TreeNode node = e.Node;
            if (node == null) return;
            NodeTag nt = node.Tag as NodeTag;
            if (nt == null) return;

            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "__loading__")
            {
                node.Nodes.Clear();
                LoadSubDirs(node, nt.Partition, nt.Cluster);
            }

            NavigateTo(nt.Partition, nt.Cluster, true);      // клик по дереву = шаг в историю
            _syncedTreeNode = node;
        }

        /// <summary>Единая точка загрузки правого списка.</summary>
        private void NavigateTo(FatxPartition part, uint cluster, bool pushBack)
        {
            if (pushBack && _curPart != null)
                _backStack.Push(new Nav(_curPart, _curCluster));
            _curPart = part;
            _curCluster = cluster;
            LoadListForCurrent();
        }

        private void LoadListForCurrent()
        {
            _list.Items.Clear();
            if (_curPart == null) return;

            // ".." — только если есть куда возвращаться
            if (_backStack.Count > 0)
            {
                ListViewItem up = new ListViewItem("..", "up");
                up.Tag = new UpTag();
                _list.Items.Add(up);
            }

            try
            {
                List<FatxDirent> entries = _curPart.ReadDirectory(_curCluster);
                foreach (FatxDirent e in entries)
                {
                    if (!e.IsValid) continue;
                    string icon = e.IsDirectory ? "folder" : DetectFileType(_curPart, e);
                    ListViewItem lvi = new ListViewItem(e.Name, icon);
                    lvi.SubItems.Add(e.IsDirectory ? "<DIR>" : ConsoleProgress.FormatSize(e.FileSize));
                    lvi.SubItems.Add(e.IsDirectory ? "Directory" : Path.GetExtension(e.Name).ToUpper().TrimStart('.'));
                    DateTime mt = e.GetLastWriteDate();
                    lvi.SubItems.Add(mt == DateTime.MinValue ? "" : mt.ToString("yyyy-MM-dd HH:mm"));
                    lvi.Tag = new ItemTag(e, _curPart);
                    _list.Items.Add(lvi);
                }
                _statusLabel.Text = _curPart.Label + " — " + entries.Count + " entries";
            }
            catch (Exception ex)
            {
                Log.Error("LoadListForCurrent: " + ex.Message);
                _statusLabel.Text = "Error reading directory.";
            }
        }

        /// <summary>Двойной клик / Enter по правому списку.</summary>
        private void OnListActivate(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0) return;
            ListViewItem lvi = _list.SelectedItems[0];

            if (lvi.Tag is UpTag) { GoUp(); return; }

            ItemTag tag = lvi.Tag as ItemTag;
            if (tag == null || !tag.Dirent.IsDirectory) return;   // файл — не открываем

            // best-effort синхронизация дерева: ищем дочернюю ноду текущей выделенной
            TreeNode target = null;
            NodeTag snt = _syncedTreeNode != null ? _syncedTreeNode.Tag as NodeTag : null;
            if (snt != null && snt.Cluster == _curCluster && snt.Partition == _curPart)
            {
                EnsureChildrenLoaded(_syncedTreeNode);
                foreach (TreeNode c in _syncedTreeNode.Nodes)
                {
                    NodeTag ct = c.Tag as NodeTag;
                    if (ct != null && ct.Cluster == tag.Dirent.FirstCluster) { target = c; break; }
                }
            }

            NavigateTo(tag.Partition, tag.Dirent.FirstCluster, true);

            _syncedTreeNode = target;
            if (target != null)
            {
                _suppressTreeSelect = true;
                try { target.Expand(); _tree.SelectedNode = target; target.EnsureVisible(); }
                finally { _suppressTreeSelect = false; }
            }
        }

        /// <summary>".." / Back.</summary>
        private void GoUp()
        {
            if (_backStack.Count == 0) return;
            Nav prev = _backStack.Pop();
            _curPart = prev.Part;
            _curCluster = prev.Cluster;
            LoadListForCurrent();

            if (_syncedTreeNode != null && _syncedTreeNode.Parent != null)
            {
                NodeTag pnt = _syncedTreeNode.Parent.Tag as NodeTag;
                if (pnt != null && pnt.Cluster == prev.Cluster && pnt.Partition == prev.Part)
                {
                    _syncedTreeNode = _syncedTreeNode.Parent;
                    _suppressTreeSelect = true;
                    try { _tree.SelectedNode = _syncedTreeNode; _syncedTreeNode.EnsureVisible(); }
                    finally { _suppressTreeSelect = false; }
                }
                else _syncedTreeNode = null;
            }
            else _syncedTreeNode = null;
        }

        private void EnsureChildrenLoaded(TreeNode node)
        {
            NodeTag nt = node.Tag as NodeTag;
            if (nt == null) return;
            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "__loading__")
            {
                node.Nodes.Clear();
                LoadSubDirs(node, nt.Partition, nt.Cluster);
            }
        }

        private void LoadFileList(TreeNode node)
        {
            _list.Items.Clear();
            if (node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;

            try
            {
                List<FatxDirent> entries = nt.Partition.ReadDirectory(nt.Cluster);
                foreach (FatxDirent e in entries)
                {
                    if (!e.IsValid) continue;
                    string icon = e.IsDirectory ? "folder" : DetectFileType(nt.Partition, e);
                    ListViewItem lvi = new ListViewItem(e.Name, icon);
                    lvi.SubItems.Add(e.IsDirectory ? "<DIR>" : ConsoleProgress.FormatSize(e.FileSize));
                    lvi.SubItems.Add(e.IsDirectory ? "Directory" : Path.GetExtension(e.Name).ToUpper().TrimStart('.'));
                    DateTime mtime = e.GetLastWriteDate();
                    lvi.SubItems.Add(mtime == DateTime.MinValue ? "" : mtime.ToString("yyyy-MM-dd HH:mm"));
                    lvi.Tag = new ItemTag(e, nt.Partition);
                    _list.Items.Add(lvi);
                }
                _statusLabel.Text = entries.Count + " entries in " + nt.Name;
            }
            catch (Exception ex)
            {
                Log.Error("LoadFileList: " + ex.Message);
                _statusLabel.Text = "Error reading directory.";
            }
        }

        private string DetectFileType(FatxPartition part, FatxDirent e)
        {
            try
            {
                if (e.FirstCluster == 0 || e.FileSize < 3) return "file";
                long off = part.ClusterToOffset(e.FirstCluster);
                part.IO.SeekTo(off);
                byte[] sig = part.IO.ReadBytes(3);
                string s = Encoding.ASCII.GetString(sig);
                if (s == "CON") return "con";
                if (s == "LIV" || s == "PIR") return "con";
            }
            catch { }
            return "file";
        }

        // ---- Context menu handlers ----

        private void OnListExtract(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0) return;

            // Собираем теги один раз
            List<ItemTag> tags = new List<ItemTag>();
            foreach (ListViewItem lvi in _list.SelectedItems)
            {
                ItemTag t = lvi.Tag as ItemTag;
                if (t != null) tags.Add(t);
            }
            if (tags.Count == 0) return;

            // Кейс "один файл" → SaveFileDialog с готовым именем
            if (tags.Count == 1 && !tags[0].Dirent.IsDirectory)
            {
                ExtractSingleFile(tags[0]);
                return;
            }

            // Кейс "пачка / папка(и)" → ОДИН выбор папки на всех
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Extract " + tags.Count + " item(s) to…";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;

                string root = fbd.SelectedPath;
                _progressBar.Visible = true;
                GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                try
                {
                    foreach (ItemTag t in tags)
                    {
                        // каждый элемент — в свою подпапку/файл внутри root
                        string dest = Path.Combine(root, FatxPartition.SanitizeName(t.Dirent.Name));
                        t.Partition.ExtractFile(t.Dirent, dest, gp); // ExtractFile сам умеет в рекурсию для папок
                    }
                    _statusLabel.Text = "Extracted " + tags.Count + " item(s) → " + root;
                    Log.Ok("Batch extract done → " + root);
                }
                catch (Exception ex)
                {
                    Log.Error("Batch extract: " + ex.Message);
                    Warn("Extract error: " + ex.Message);
                }
                finally { _progressBar.Visible = false; _progressBar.Value = 0; }
            }
        }

        /// <summary>Одиночный файл → SaveFileDialog (старое поведение, вынесено отдельно).</summary>
        private void ExtractSingleFile(ItemTag tag)
        {
            FatxDirent d = tag.Dirent;
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.FileName = FatxPartition.SanitizeName(d.Name);
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _progressBar.Visible = true;
                    GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                    tag.Partition.ExtractFile(d, sfd.FileName, gp);
                    _statusLabel.Text = "Extracted: " + d.Name;
                }
                catch (Exception ex) { Warn("Extract error: " + ex.Message); }
                finally { _progressBar.Visible = false; _progressBar.Value = 0; }
            }
        }

        private void OnTreeExtract(object sender, EventArgs e)
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;
            // extract entire directory
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select extraction destination";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _progressBar.Visible = true;
                    GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                    List<FatxDirent> entries = nt.Partition.ReadDirectory(nt.Cluster);
                    foreach (FatxDirent d in entries)
                    {
                        if (!d.IsValid) continue;
                        string dest = Path.Combine(fbd.SelectedPath, FatxPartition.SanitizeName(d.Name));
                        nt.Partition.ExtractFile(d, dest, gp);
                    }
                    Log.Ok("Extraction complete.");
                    _statusLabel.Text = "Extraction complete.";
                }
                catch (Exception ex)
                {
                    Log.Error("Extract: " + ex.Message);
                    Warn("Extraction error: " + ex.Message);
                }
                finally { _progressBar.Visible = false; _progressBar.Value = 0; }
            }
        }

        private void ExtractDirent(ItemTag tag)
        {
            FatxDirent d = tag.Dirent;
            FatxPartition part = tag.Partition;

            if (d.IsDirectory)
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "Extract folder '" + d.Name + "' to…";
                    if (fbd.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        _progressBar.Visible = true;
                        GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                        string dest = Path.Combine(fbd.SelectedPath, FatxPartition.SanitizeName(d.Name));
                        part.ExtractDirectoryRecursive(d, dest, gp);
                        _statusLabel.Text = "Done.";
                    }
                    catch (Exception ex) { Warn("Extract error: " + ex.Message); }
                    finally { _progressBar.Visible = false; _progressBar.Value = 0; }
                }
            }
            else
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.FileName = FatxPartition.SanitizeName(d.Name);
                    if (sfd.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        _progressBar.Visible = true;
                        GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                        part.ExtractFile(d, sfd.FileName, gp);
                        _statusLabel.Text = "Extracted: " + d.Name;
                    }
                    catch (Exception ex) { Warn("Extract error: " + ex.Message); }
                    finally { _progressBar.Visible = false; _progressBar.Value = 0; }
                }
            }
        }

        private void OnListDelete(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count == 0) return;
            if (MessageBox.Show(this, "Delete selected item(s)?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            foreach (ListViewItem lvi in _list.SelectedItems)
            {
                ItemTag tag = lvi.Tag as ItemTag;
                if (tag == null) continue;
                try
                {
                    if (tag.Dirent.IsDirectory && tag.Dirent.FirstCluster != 0)
                    {
                        // recursive delete
                        DeleteDirRecursive(tag.Partition, tag.Dirent);
                    }
                    tag.Partition.DeleteDirent(tag.Dirent);
                }
                catch (Exception ex) { Warn("Delete error: " + ex.Message); }
            }
            RefreshCurrentNode();
        }

        private void DeleteDirRecursive(FatxPartition part, FatxDirent dir)
        {
            List<FatxDirent> entries = part.ReadDirectory(dir.FirstCluster);
            foreach (FatxDirent e in entries)
            {
                if (!e.IsValid) continue;
                if (e.IsDirectory && e.FirstCluster != 0)
                    DeleteDirRecursive(part, e);
                else if (e.FirstCluster != 0)
                    part.FreeChain(e.FirstCluster);
                e.MarkDeleted(part.IO);
            }
            part.FreeChain(dir.FirstCluster);
        }

        private void OnTreeDelete(object sender, EventArgs e)
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null || node.Parent == null) return;
            if (MessageBox.Show(this, "Delete '" + node.Text + "'?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            // We need the parent's cluster to find the dirent
            // For simplicity, just warn
            Warn("Use the file list (right panel) to delete items.");
        }

        private void OnListRename(object sender, EventArgs e)
        {
            if (_list.SelectedItems.Count != 1) return;
            ItemTag tag = _list.SelectedItems[0].Tag as ItemTag;
            if (tag == null) return;

            using (InputDialog dlg = new InputDialog("Rename", "New name:", tag.Dirent.Name))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    tag.Partition.RenameDirent(tag.Dirent, dlg.InputValue);
                    RefreshCurrentNode();
                }
                catch (Exception ex) { Warn("Rename error: " + ex.Message); }
            }
        }

        private void OnTreeInjectFile(object sender, EventArgs e)
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Multiselect = true;
                ofd.Title = "Inject file(s) into " + nt.Name;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _progressBar.Visible = true;
                    GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                    foreach (string f in ofd.FileNames)
                        nt.Partition.InjectFile(f, nt.Cluster, gp);
                    RefreshCurrentNode();
                    _statusLabel.Text = "Injection complete.";
                }
                catch (Exception ex) { Warn("Inject error: " + ex.Message); }
                finally { _progressBar.Visible = false; _progressBar.Value = 0; }
            }
        }

        private void OnTreeInjectFolder(object sender, EventArgs e)
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;

            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select folder to inject";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _progressBar.Visible = true;
                    GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                    nt.Partition.InjectFolder(fbd.SelectedPath, nt.Cluster, gp);
                    RefreshCurrentNode();
                    _statusLabel.Text = "Folder injection complete.";
                }
                catch (Exception ex) { Warn("Inject folder error: " + ex.Message); }
                finally { _progressBar.Visible = false; _progressBar.Value = 0; }
            }
        }

        private void OnTreeNewFolder(object sender, EventArgs e)
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;

            using (InputDialog dlg = new InputDialog("New Folder", "Folder name:", "NewFolder"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    nt.Partition.CreateFolder(dlg.InputValue, nt.Cluster);
                    RefreshCurrentNode();
                }
                catch (Exception ex) { Warn("Create folder error: " + ex.Message); }
            }
        }

        private void OnListDragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || _tree.SelectedNode == null || _tree.SelectedNode.Tag == null) return;
            NodeTag nt = (NodeTag)_tree.SelectedNode.Tag;

            try
            {
                _progressBar.Visible = true;
                GuiProgress gp = new GuiProgress(_progressBar, _statusLabel);
                foreach (string f in files)
                {
                    if (Directory.Exists(f))
                        nt.Partition.InjectFolder(f, nt.Cluster, gp);
                    else if (File.Exists(f))
                        nt.Partition.InjectFile(f, nt.Cluster, gp);
                }
                RefreshCurrentNode();
                _statusLabel.Text = "Drag-drop injection complete.";
            }
            catch (Exception ex) { Warn("Drag-drop error: " + ex.Message); }
            finally { _progressBar.Visible = false; _progressBar.Value = 0; }
        }

        private void RefreshCurrentNode()
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null || node.Tag == null) return;
            NodeTag nt = (NodeTag)node.Tag;

            node.Nodes.Clear();
            LoadSubDirs(node, nt.Partition, nt.Cluster);

            NavigateTo(nt.Partition, nt.Cluster, false);   // без записи в историю
            _syncedTreeNode = node;
        }

        private void Warn(string msg)
        {
            Log.Warn(msg);
            MessageBox.Show(this, msg, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ClosePartitions();
            base.OnFormClosing(e);
        }
    }

    // ---- About Form ----
    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About Xbox 360 USB Explorer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(420, 300);
            ShowInTaskbar = false;

            TableLayoutPanel tbl = new TableLayoutPanel();
            tbl.Dock = DockStyle.Fill;
            tbl.ColumnCount = 1;
            tbl.RowCount = 6;
            tbl.Padding = new Padding(12);

            Label title = new Label();
            title.Text = "Xbox 360 USB Explorer v3.0";
            title.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleCenter;

            Label desc = new Label();
            desc.Text = "FATX/XTAF file-system explorer for Xbox 360\nUSB drives, HDD images, and raw HDDs.";
            desc.Dock = DockStyle.Fill;
            desc.TextAlign = ContentAlignment.MiddleCenter;

            Label credits = new Label();
            credits.Text = "Original: Slasher / Darkjump (2015)\nRewrite: 2026\nFATX docs: free60.org, aerosoul94";
            credits.Dock = DockStyle.Fill;
            credits.TextAlign = ContentAlignment.MiddleCenter;

            RichTextBox rtb = new RichTextBox();
            rtb.ReadOnly = true;
            rtb.Dock = DockStyle.Fill;
            rtb.Text = "Features:\n" +
                "• USB drive (Data0000/Data0001+) support\n" +
                "• HDD image and raw HDD support\n" +
                "• Extract / Inject / Delete / Rename\n" +
                "• Recursive folder operations\n" +
                "• Large file support (buffered I/O)\n" +
                "• Console TUI mode with progress bars\n" +
                "• Coloured logging to console + file\n" +
                "• No external dependencies";

            Button ok = new Button();
            ok.Text = "OK";
            ok.Dock = DockStyle.Fill;
            ok.Click += delegate { Close(); };

            tbl.Controls.Add(title, 0, 0);
            tbl.Controls.Add(desc, 0, 1);
            tbl.Controls.Add(credits, 0, 2);
            tbl.Controls.Add(rtb, 0, 3);
            tbl.Controls.Add(ok, 0, 5);
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 12));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 12));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 12));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 0));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 12));

            Controls.Add(tbl);
            AcceptButton = ok;
        }
    }

    // ---- Input Dialog (rename / new folder) ----
    internal sealed class InputDialog : Form
    {
        private TextBox _textBox;
        public string InputValue { get { return _textBox.Text.Trim(); } }

        public InputDialog(string title, string label, string defaultValue)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(320, 120);
            ShowInTaskbar = false;

            Label lbl = new Label();
            lbl.Text = label;
            lbl.Location = new Point(12, 15);
            lbl.AutoSize = true;

            _textBox = new TextBox();
            _textBox.Text = defaultValue;
            _textBox.Location = new Point(12, 35);
            _textBox.Width = 280;

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(130, 65);
            ok.Width = 75;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(215, 65);
            cancel.Width = 75;

            Controls.Add(lbl);
            Controls.Add(_textBox);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ========================================================================
    //  12. CONSOLE MODE  (TUI)
    // ========================================================================
    internal static class ConsoleMode
    {
        public static void Run(string[] args)
        {
            Tui.SetTitle("Console Mode");
            Tui.Header("Xbox 360 USB Explorer — Console Mode");
            Console.WriteLine();

            // Parse console args
            string action = null;
            string source = null;
            string dest = null;
            bool recursive = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--extract" || a == "-x") { action = "extract"; }
                else if (a == "--inject" || a == "-i") { action = "inject"; }
                else if (a == "--list" || a == "-l") { action = "list"; }
                else if (a == "--source" || a == "-s") { if (i + 1 < args.Length) source = args[++i]; }
                else if (a == "--dest" || a == "-d") { if (i + 1 < args.Length) dest = args[++i]; }
                else if (a == "--recursive" || a == "-r") { recursive = true; }
            }

            if (action == null)
            {
                InteractiveMenu();
                return;
            }

            // Auto-detect source if not specified
            if (source == null)
            {
                string xbox = DeviceDetect.FindXbox360Folder();
                if (xbox != null)
                {
                    string[] dataFiles = DeviceDetect.FindDataFiles(xbox);
                    if (dataFiles.Length > 0) source = dataFiles[0];
                }
            }

            if (source == null)
            {
                Log.Error("No source specified and no Xbox360 folder found.");
                return;
            }

            try
            {
                PartitionStream io;
                long partLen;
                long partOff = 0;

                if (File.Exists(source))
                {
                    io = new PartitionStream(source);
                    partLen = new FileInfo(source).Length;
                }
                else
                {
                    Log.Error("Source not found: " + source);
                    return;
                }

                using (FatxPartition part = new FatxPartition(io, partOff, partLen, "CLI"))
                {
                    if (action == "list")
                    {
                        ListDir(part, part.Header.RootCluster, "", recursive);
                    }
                    else if (action == "extract")
                    {
                        if (dest == null) dest = Path.Combine(Environment.CurrentDirectory, "extracted");
                        ConsoleProgress cp = new ConsoleProgress("Extracting");
                        List<FatxDirent> entries = part.ReadDirectory(part.Header.RootCluster);
                        foreach (FatxDirent e in entries)
                        {
                            if (!e.IsValid) continue;
                            string outPath = Path.Combine(dest, FatxPartition.SanitizeName(e.Name));
                            part.ExtractFile(e, outPath, cp);
                        }
                        Log.Ok("Extraction complete → " + dest);
                    }
                    else if (action == "inject")
                    {
                        if (dest == null)
                        {
                            Log.Error("Specify --dest <file/folder> for injection.");
                            return;
                        }
                        ConsoleProgress cp = new ConsoleProgress("Injecting");
                        if (Directory.Exists(dest))
                            part.InjectFolder(dest, part.Header.RootCluster, cp);
                        else if (File.Exists(dest))
                            part.InjectFile(dest, part.Header.RootCluster, cp);
                        else
                            Log.Error("Dest not found: " + dest);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Console mode error: " + ex.Message);
            }
        }

        private static void ListDir(FatxPartition part, uint cluster, string indent, bool recursive)
        {
            List<FatxDirent> entries = part.ReadDirectory(cluster);
            foreach (FatxDirent e in entries)
            {
                if (!e.IsValid) continue;
                ConsoleColor c = e.IsDirectory ? ConsoleColor.Yellow : ConsoleColor.White;
                Console.ForegroundColor = c;
                Console.WriteLine("{0}{1,-42} {2,12}  {3}",
                    indent,
                    e.Name,
                    e.IsDirectory ? "<DIR>" : ConsoleProgress.FormatSize(e.FileSize),
                    e.GetLastWriteDate().ToString("yyyy-MM-dd HH:mm"));
                Console.ResetColor();

                if (recursive && e.IsDirectory && e.FirstCluster != 0)
                    ListDir(part, e.FirstCluster, indent + "  ", true);
            }
        }

        private static void InteractiveMenu()
        {
            while (true)
            {
                int sel = Tui.ShowMenu("Main Menu", new string[]
                {
                    "Auto-detect & List USB drive",
                    "Open HDD image & List",
                    "Extract all (USB)",
                    "Exit"
                });

                if (sel < 0 || sel == 3) break;

                try
                {
                    if (sel == 0)
                    {
                        string xbox = DeviceDetect.FindXbox360Folder();
                        if (xbox == null) { Log.Warn("No Xbox360 folder found."); continue; }
                        string[] dataFiles = DeviceDetect.FindDataFiles(xbox);
                        if (dataFiles.Length == 0) { Log.Warn("No data files."); continue; }
                        PartitionStream io = new PartitionStream(dataFiles);
                        long len = 0;
                        foreach (string df in dataFiles) len += new FileInfo(df).Length;
                        using (FatxPartition part = new FatxPartition(io, 0, len, "USB"))
                        {
                            Console.WriteLine();
                            ListDir(part, part.Header.RootCluster, "", true);
                        }
                    }
                    else if (sel == 1)
                    {
                        string path = Tui.Prompt("HDD image path");
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log.Warn("File not found."); continue; }
                        long fileLen = new FileInfo(path).Length;
                        PartitionStream io = new PartitionStream(path);
                        using (FatxPartition part = new FatxPartition(io, FatxConst.HDD_DATA_OFFSET,
                            fileLen - FatxConst.HDD_DATA_OFFSET, "HDD"))
                        {
                            Console.WriteLine();
                            ListDir(part, part.Header.RootCluster, "", true);
                        }
                    }
                    else if (sel == 2)
                    {
                        string xbox = DeviceDetect.FindXbox360Folder();
                        if (xbox == null) { Log.Warn("No Xbox360 folder found."); continue; }
                        string[] dataFiles = DeviceDetect.FindDataFiles(xbox);
                        if (dataFiles.Length == 0) { Log.Warn("No data files."); continue; }
                        string dest = Tui.Prompt("Destination folder");
                        if (string.IsNullOrEmpty(dest)) dest = Path.Combine(Environment.CurrentDirectory, "extracted");
                        PartitionStream io = new PartitionStream(dataFiles);
                        long len = 0;
                        foreach (string df in dataFiles) len += new FileInfo(df).Length;
                        using (FatxPartition part = new FatxPartition(io, 0, len, "USB"))
                        {
                            ConsoleProgress cp = new ConsoleProgress("Extract");
                            List<FatxDirent> entries = part.ReadDirectory(part.Header.RootCluster);
                            foreach (FatxDirent e in entries)
                            {
                                if (!e.IsValid) continue;
                                string outPath = Path.Combine(dest, FatxPartition.SanitizeName(e.Name));
                                part.ExtractFile(e, outPath, cp);
                            }
                        }
                        Log.Ok("Done → " + dest);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Menu action error: " + ex.Message);
                }

                Console.WriteLine();
                Console.WriteLine("  Press any key to continue…");
                Console.ReadKey(true);
            }
        }
    }

    // ========================================================================
    //  13. PROGRAM ENTRY POINT
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // --- Logging init ---
            Log.Init();
            Log.Info("=== Xbox 360 USB Explorer v3.0 ===");
            Log.Info("Runtime: " + Environment.Version);
            Log.Info("OS: " + Environment.OSVersion);
            Log.Info("Admin: " + Win32Native.IsAdministrator());

            // --- Parse global flags ---
            bool guiMode = false;
            bool consoleMode = false;
            bool showHelp = false;

            foreach (string a in args)
            {
                string la = a.ToLowerInvariant();
                if (la == "--gui" || la == "-g") guiMode = true;
                else if (la == "--console" || la == "-c") consoleMode = true;
                else if (la == "--help" || la == "-h" || la == "/?" || la == "-?") showHelp = true;
            }

            if (showHelp)
            {
                PrintHelp();
                Log.Shutdown();
                return;
            }

            // Default: GUI if no console-specific args
            if (!consoleMode && !guiMode)
            {
                // If there are action args (--extract, --inject, --list), use console
                foreach (string a in args)
                {
                    string la = a.ToLowerInvariant();
                    if (la == "--extract" || la == "-x" || la == "--inject" || la == "-i" || la == "--list" || la == "-l")
                    {
                        consoleMode = true;
                        break;
                    }
                }
            }

            if (consoleMode)
            {
                ConsoleMode.Run(args);
            }
            else
            {
                // GUI mode
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Info("Starting GUI…");
                Tui.SetTitle("GUI");
                Application.Run(new MainForm());
            }

            Log.Info("Exiting.");
            Log.Shutdown();
        }

        private static void PrintHelp()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  Xbox 360 USB Explorer v3.0");
            Console.WriteLine("  ==========================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  USAGE:");
            Console.WriteLine("    Xbox360_USB_Explorer.exe [options]");
            Console.WriteLine();
            Console.WriteLine("  MODES:");
            Console.WriteLine("    --gui, -g          Launch WinForms GUI (default)");
            Console.WriteLine("    --console, -c      Launch console TUI mode");
            Console.WriteLine();
            Console.WriteLine("  CONSOLE ACTIONS:");
            Console.WriteLine("    --list, -l         List FATX directory contents");
            Console.WriteLine("    --extract, -x      Extract files from FATX partition");
            Console.WriteLine("    --inject, -i       Inject file/folder into FATX partition");
            Console.WriteLine();
            Console.WriteLine("  OPTIONS:");
            Console.WriteLine("    --source, -s <path>   Source device/image/path");
            Console.WriteLine("    --dest, -d <path>     Destination path");
            Console.WriteLine("    --recursive, -r       Recursive operation");
            Console.WriteLine("    --help, -h, /?        Show this help");
            Console.WriteLine();
            Console.WriteLine("  EXAMPLES:");
            Console.WriteLine("    Xbox360_USB_Explorer.exe --gui");
            Console.WriteLine("    Xbox360_USB_Explorer.exe -c -l -r");
            Console.WriteLine("    Xbox360_USB_Explorer.exe -x -s D:\\Xbox360\\Data0001 -d C:\\out");
            Console.WriteLine("    Xbox360_USB_Explorer.exe -i -s image.bin -d C:\\myfile.dat");
            Console.WriteLine();
            Console.WriteLine("  SUPPORTED DEVICES:");
            Console.WriteLine("    • USB drives with Xbox360 folder (Data0000, Data0001+)");
            Console.WriteLine("    • HDD image files (raw binary dumps)");
            Console.WriteLine("    • Raw physical HDD (requires Administrator)");
            Console.WriteLine();
            Console.WriteLine("  FILE SYSTEM: FATX / XTAF (Xbox 360)");
            Console.WriteLine("  Reference: https://free60.org/System-Software/Systems/FATX/");
            Console.WriteLine();
        }
    }
}