using Colossal.Serialization.Entities;
using Unity.Entities;

namespace Traffic.Components
{
    public struct DataOwner: IComponentData, ISerializable
    {
        public Entity entity;

        public DataOwner(Entity owner) {
            entity = owner;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving DataOwner: {entity}");
            writer.Write(1);//data version
            writer.Write(entity);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);
            reader.Read(out entity);
            Logger.Serialization($"Reading DataOwner({v}): {entity}");
        }
    }
}
