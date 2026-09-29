using LiteNetLib.Utils;

namespace LiteNetLibManager
{
    public struct ServerErrorMessage : INetSerializable
    {
        public bool shouldDisconnect;
        public string errorMessage;

        public void Deserialize(NetDataReader reader)
        {
            shouldDisconnect = reader.GetBool();
            errorMessage = reader.GetString(NetDataReaderExtension.MaxStringLength);
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(shouldDisconnect);
            writer.Put(errorMessage);
        }
    }
}