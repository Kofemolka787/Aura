using System;
using System.IO;
using System.Text;

namespace ColorForge.Services;

public sealed class LiveCsvReader : IDisposable
{
    private readonly string _path;
    private readonly LiveFrameTail _tail;
    private FileStream? _input;
    private readonly byte[] _bytes = new byte[65536];
    private readonly char[] _chars = new char[Encoding.UTF8.GetMaxCharCount(65536)];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    public LiveCsvReader(string path, string process) { _path = path; _tail = new(process); }
    public LiveFrameSnapshot Read(DateTime now)
    {
        if (_input == null && File.Exists(_path)) _input = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_input != null)
        {
            if (_input.Length > 128 * 1024 * 1024) throw new InvalidDataException("Живой CSV превысил лимит.");
            for (int batch = 0; batch < 4 && _input.Position < _input.Length; batch++)
            {
                int count = _input.Read(_bytes, 0, _bytes.Length);
                if (count == 0) break;
                int decoded = _decoder.GetChars(_bytes, 0, count, _chars, 0);
                _tail.Append(new string(_chars, 0, decoded), now);
            }
        }
        return _tail.Snapshot(now);
    }
    public void Dispose() => _input?.Dispose();
}
