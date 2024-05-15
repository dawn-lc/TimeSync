using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
namespace TimeSync
{
    public enum Mode
    {
        Reserved = 0x00,
        SymmetricActive = 0x01,
        SymmetricPassive = 0x02,
        Client = 0x03,
        Server = 0x04,
        Broadcast = 0x05,
        ControlMessage = 0x06,
        PrivateUse = 0x07,
        Unknown = 0xFF
    }

    public enum LeapIndicator
    {
        NoWarning = 0x00,
        LastMinute61 = 0x01,
        LastMinute59 = 0x02,
        Alarm = 0x03,
    }

    public enum Stratum
    {
        Unspecified = 0,
        PrimaryReference = 1,
        SecondaryReference = 2,
        Reserved = 3
    }

    public class NTPPacket
    {
        private static readonly DateTime StartOfCentury = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const uint TicksPerSecond = 1000U;
        private const ulong TimestampFactor = 0x100000000L;
        private byte[] rawData = new byte[48];
        public byte[] RawData 
        {
            get => rawData;
            set
            {
                if (ToDateTime(value, 24) == StartOfCentury) Array.Copy(rawData, 24, value, 24, 8);
                rawData = value;
            }
        }

        public LeapIndicator LeapIndicator
        {
            get => Enum.IsDefined(typeof(LeapIndicator), (RawData[0] & 0b11000000) >> 6) ? (LeapIndicator)((RawData[0] & 0b11000000) >> 6) : LeapIndicator.Alarm;
            set => RawData[0] = (byte)((RawData[0] & 0b00111111) | (byte)value << 6);
        }
        public int VersionNumber
        {
            get => (RawData[0] & 0b00111000) >> 3;
            set => RawData[0] = (byte)((RawData[0] & 0b11000111) | (byte)value << 3);
        }
        public Mode Mode
        {
            get => Enum.IsDefined(typeof(Mode), RawData[0] & 0b00000111) ? (Mode)(RawData[0] & 0x7) : Mode.Unknown;
            set => RawData[0] = (byte)((RawData[0] & 0b11111000) | (byte)value);
        }

        public Stratum Stratum
        {
            get
            {
                if (RawData[1] == 0) return Stratum.Unspecified;
                else if (RawData[1] == 1) return Stratum.PrimaryReference;
                else if (RawData[1] <= 15) return Stratum.SecondaryReference;
                else return Stratum.Reserved;
            }
        }
        public sbyte Poll => (sbyte)RawData[2];
        public sbyte Precision => (sbyte)RawData[3];
        public uint RootDelay => BitConverter.ToUInt32(RawData.Skip(4).Take(4).Reverse().ToArray(), 0);
        public uint RootDispersion => BitConverter.ToUInt32(RawData.Skip(8).Take(4).Reverse().ToArray(), 0);
        public byte[] ReferenceID => RawData.Skip(12).Take(4).Reverse().ToArray();
        public DateTime ReferenceDateTime
        {
            get => ToDateTime(RawData, 16);
            set => Array.Copy(ToNTPTimestamp(value), 0, RawData, 16, 8);
        }
        public DateTime OriginateDateTime
        {
            get => ToDateTime(RawData, 24);
            set => Array.Copy(ToNTPTimestamp(value), 0, RawData, 24, 8);
        }
        public DateTime ReceiveDateTime
        {
            get => ToDateTime(RawData, 32);
            set => Array.Copy(ToNTPTimestamp(value), 0, RawData, 32, 8);
        }
        public DateTime TransmitDateTime
        {
            get => ToDateTime(RawData, 40);
            set => Array.Copy(ToNTPTimestamp(value), 0, RawData, 40, 8);
        }
        public NTPPacket(byte[] bytes = null)
        {
            RawData = bytes ?? new byte[48];
        }
        public static DateTime ToDateTime(byte[] bytes, int offset)
        {
            uint seconds = BitConverter.ToUInt32(bytes.Skip(offset).Take(4).Reverse().ToArray(), 0);
            uint fraction = BitConverter.ToUInt32(bytes.Skip(offset + 4).Take(4).Reverse().ToArray(), 0);
            return StartOfCentury.AddSeconds(seconds).AddMilliseconds(fraction * TicksPerSecond / TimestampFactor);
        }
        public static byte[] ToNTPTimestamp(DateTime dateTime)
        {
            TimeSpan span = dateTime - StartOfCentury;
            uint seconds = (uint)span.TotalSeconds;
            uint fraction = (uint)(span.TotalMilliseconds % TicksPerSecond * TimestampFactor);
            return BitConverter.GetBytes(seconds).Reverse().Concat(BitConverter.GetBytes(fraction).Reverse()).ToArray();
        }
    }
    public class NTPClient : IDisposable
    {
        NTPPacket Packet;
        public DateTime DestinationDateTime { get; set; }
        public TimeSpan RoundTripDelay => DestinationDateTime - Packet.OriginateDateTime - (Packet.ReceiveDateTime - Packet.TransmitDateTime);
        public TimeSpan ClockOffset => TimeSpan.FromMilliseconds((Packet.ReceiveDateTime - Packet.OriginateDateTime + (Packet.TransmitDateTime - DestinationDateTime)).TotalMilliseconds / 2);

        UdpClient TimeSocket { get; set; }
        IPAddress IP { get; set; }
        public NTPClient(string host, int timeOut = 3000)
        {
            IP = Dns.GetHostEntry(host).AddressList.First(address => address.AddressFamily == AddressFamily.InterNetwork);
            TimeSocket = new UdpClient() { Client = { SendTimeout = timeOut, ReceiveTimeout = timeOut } };
        }
        public DateTime Query()
        {
            try
            {
                IPEndPoint EPhost = new IPEndPoint(IP, 123);
                TimeSocket.Connect(EPhost);
                Packet = new NTPPacket() { Mode = Mode.Client, VersionNumber = 3, OriginateDateTime = DateTime.UtcNow };
                TimeSocket.Send(Packet.RawData, Packet.RawData.Length);
                Packet.RawData = TimeSocket.Receive(ref EPhost);
                DestinationDateTime = DateTime.UtcNow;
                return DateTime.UtcNow.Add(ClockOffset).ToLocalTime();
            }
            catch (SocketException e)
            {
                throw new Exception(e.Message);
            }
        }
        public void Dispose()
        {
            ((IDisposable)TimeSocket).Dispose();
        }
    }
}
