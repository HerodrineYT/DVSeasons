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
            State.WriteTo(writer);
        }

        public void Deserialize(BinaryReader reader)
        {
            State = SeasonNetworkState.ReadFrom(reader);
        }
    }

    public sealed class SeasonStateRequestPacket : ISerializablePacket
    {
        public int Protocol = SeasonNetworkState.CurrentProtocol;
        public void Serialize(BinaryWriter writer) { writer.Write(Protocol); }
        public void Deserialize(BinaryReader reader) { Protocol = reader.ReadInt32(); }
    }
}
