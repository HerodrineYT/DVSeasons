using System;
using System.Collections.Generic;
using System.IO;
using DVSeasons.Core;
using MPAPI.Interfaces.Packets;

namespace DVSeasons.Multiplayer
{
    public sealed class CabHeaterChangePacket : ISerializablePacket
    {
        public string CarId = "";
        public float Level;
        public void Serialize(BinaryWriter writer) {writer.Write(CarId);writer.Write(Level);}
        public void Deserialize(BinaryReader reader)
        {
            CarId=reader.ReadString();Level=reader.ReadSingle();
            if(CarId.Length>80 || !CabHeaterSetting.IsAllowed(Level)) throw new InvalidDataException("Invalid heater change");
        }
    }
    public sealed class CabHeaterSnapshotPacket : ISerializablePacket
    {
        public Dictionary<string,float> States = new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);
        public void Serialize(BinaryWriter writer)
        {
            writer.Write(States.Count);
            foreach(var pair in States) {writer.Write(pair.Key);writer.Write(pair.Value);}
        }
        public void Deserialize(BinaryReader reader)
        {
            int count=reader.ReadInt32(); if(count<0 || count>1024) throw new InvalidDataException("Invalid heater count");
            States.Clear();
            for(int i=0;i<count;i++)
            {
                var id=reader.ReadString();float value=reader.ReadSingle();
                if(string.IsNullOrEmpty(id) || id.Length>80 || !CabHeaterSetting.IsAllowed(value)) throw new InvalidDataException("Invalid heater state");
                States[id]=value;
            }
        }
    }
}
