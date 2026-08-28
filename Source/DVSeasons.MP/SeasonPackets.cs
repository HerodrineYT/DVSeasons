using System.IO;
using DVSeasons.Core;
using MPAPI.Interfaces.Packets;

namespace DVSeasons.Multiplayer
{
    public sealed class SeasonStatePacket : ISerializablePacket
    {
        public SeasonNetworkState State = new SeasonNetworkState();

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(State.Protocol);
            writer.Write(State.Sequence);
            writer.Write(State.Phase);
            writer.Write((byte)State.Current);
            writer.Write((byte)State.Next);
            writer.Write(State.Transition);
            writer.Write(State.SnowAmount);
            writer.Write(State.TemperatureCelsius);
            writer.Write(State.WinterWetnessEquivalent);
        }

        public void Deserialize(BinaryReader reader)
        {
            State = new SeasonNetworkState
            {
                Protocol = reader.ReadInt32(),
                Sequence = reader.ReadUInt32(),
                Phase = reader.ReadDouble(),
                Current = (SeasonKind)reader.ReadByte(),
                Next = (SeasonKind)reader.ReadByte(),
                Transition = reader.ReadSingle(),
                SnowAmount = reader.ReadSingle(),
                TemperatureCelsius = reader.ReadSingle(),
                WinterWetnessEquivalent = reader.ReadSingle()
            };
        }
    }

    public sealed class SeasonStateRequestPacket : ISerializablePacket
    {
        public int Protocol = SeasonNetworkState.CurrentProtocol;
        public void Serialize(BinaryWriter writer) { writer.Write(Protocol); }
        public void Deserialize(BinaryReader reader) { Protocol = reader.ReadInt32(); }
    }
}
