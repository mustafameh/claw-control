using System.Collections.Concurrent;
namespace HidSharp
{
    public class DeviceList
    {
        public static DeviceList Local { get; } = new();
        public event EventHandler? Changed { add {} remove {} }
        public HidDevice[] GetHidDevices(int vid) => [new()];
    }
    public class HidDevice
    {
        public int GetMaxOutputReportLength() => 64;
        public Descriptor GetReportDescriptor() => new();
        public bool TryOpen(out HidStream stream) { stream = new(); return true; }
    }
    public class Descriptor { public Report[] OutputReports => [new()]; }
    public class Report { public byte ReportID => 15; }
    public class HidStream : IDisposable
    {
        public static byte[] Ram = new byte[1478], Rom = new byte[1478];
        public static bool FailWrite, FailSave, FailAfterWrite, IgnoreWrite;
        public int ReadTimeout { get; set; }
        readonly BlockingCollection<byte[]> replies = new();
        public int Read(byte[] buffer, int offset, int length)
        {
            var reply = replies.Take();
            reply.CopyTo(buffer, offset);
            return reply.Length;
        }
        public void Write(byte[] msg)
        {
            byte[] reply = new byte[64];
            int offset = (msg[6] << 8) | msg[7];
            int length = msg[8];
            switch (msg[4])
            {
                case 38: reply[4] = 39; reply[5] = 1; break;
                case 4:
                    reply[4] = 5;
                    Array.Copy(msg, 6, reply, 6, 3);
                    Array.Copy(Ram, offset, reply, 9, length);
                    break;
                case 0x21:
                    if (FailWrite) throw new IOException("injected profile write failure");
                    if (!IgnoreWrite) Array.Copy(msg, 9, Ram, offset, length);
                    if (FailAfterWrite) throw new IOException("Simulated lost write acknowledgement");
                    break;
                case 34:
                    if (FailSave) throw new IOException("injected save failure");
                    Rom = Ram.ToArray();
                    break;
                default: throw new InvalidOperationException($"Unexpected command {msg[4]}");
            }
            replies.Add(reply);
        }
        public void Dispose() => replies.CompleteAdding();
    }
}
